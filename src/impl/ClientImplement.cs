using Google.Protobuf;
using NativeWebSocket;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;

namespace MyConnection;

public class ClientImplement : ClientAbstract
{
    private IWebSocketClient? _ws;
    private Task? _connectTask;
    private readonly SubjectDispatcher _tcpDispatcher = new();
    private readonly SubjectDispatcher _udpDispatcher = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<Action> _onDisconnectCallbacks = new();
    private readonly List<Action<WarningInfo>> _onWarningCallbacks = new();
    private readonly object _gate = new();

    private UdpClientWrapper? _udpClient;
    private UdpPingService? _udpPing;
    private TaskCompletionSource<bool> _udpReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string>? _udpAuthKeyTcs;
    private CancellationTokenSource? _udpHandshakeCts;
    private int _udpPingIntervalMs;
    private int _udpPingTimeoutMs;

    private HttpClient? _http;
    private Func<Task<byte[]>>? _reLoginFactory;
    private object? _loginDataFactory;

    public ClientImplement(ClientConfig config)
    {
        _config = config;
        _udpPingIntervalMs = config.udpPingIntervalMs > 0 ? config.udpPingIntervalMs : 5000;
        _udpPingTimeoutMs = config.udpPingTimeoutMs > 0 ? config.udpPingTimeoutMs : 15000;

        _tcpDispatcher.OnEmptyDispatch += sub => FireWarning("W006", $"Tin nhắn TCP bị rơi, không có subscriber cho subject '{sub}'");
        _udpDispatcher.OnEmptyDispatch += sub => FireWarning("W007", $"Tin nhắn UDP bị rơi, không có subscriber cho subject '{sub}'");
    }

    internal void SetToken(string token) => _token = token;

    protected override async Task ConnectWebSocket(string token, string websocketServer)
    {
        _udpReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var uri = new Uri(
            websocketServer.Contains("://") ? websocketServer : "ws://" + websocketServer);

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token
        };

        _ws = new NativeWebSocketClient(uri.ToString(), headers);

        _ws = new SystemWebSocketClient(uri, headers);

        var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ws.OnOpen += () =>
        {
            connectTcs.TrySetResult(true);
        };

        _ws.OnError += (err) =>
        {
            if (!connectTcs.Task.IsCompletedSuccessfully)
                connectTcs.TrySetException(new ConnectionFailedException(err ?? "Unknown connection error"));
        };

        _ws.OnMessage += OnMessage;

        _ws.OnClose += (code) =>
        {
            if (!connectTcs.Task.IsCompletedSuccessfully)
            {
                connectTcs.TrySetException(new ConnectionFailedException($"WebSocket closed with code {code}"));
                return;
            }

            FireWarning("W005", $"WebSocket đã đóng (mã: {code})");

            Action[] snapshot;
            lock (_gate) { snapshot = _onDisconnectCallbacks.ToArray(); }
            foreach (var cb in snapshot) cb();
        };

        _connectTask = _ws.ConnectAsync();

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        var completedTask = await Task.WhenAny(connectTcs.Task, timeoutTask);
        if (completedTask == timeoutTask)
        {
            _ws.CancelConnection();
            throw new ConnectionFailedException("Connection timed out after 10 seconds");
        }
        await completedTask;
    }

    private void OnMessage(byte[] data)
    {
        try
        {
            var envelope = MessageEnvelope.Parser.ParseFrom(data);

            if (envelope.Subject == "__udp_auth__")
            {
                var key = Encoding.UTF8.GetString(envelope.Payload.ToByteArray());
                _udpAuthKeyTcs?.TrySetResult(key);
                return;
            }

            if (envelope.Subject == "__udp_bound__")
            {
                _udpReadyTcs.TrySetResult(true);
                return;
            }

            _tcpDispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
        }
        catch { }
    }

    protected override async void NotifyConnectUdp(string token, string udpServer)
    {
        if (string.IsNullOrEmpty(udpServer)) return;

        try
        {
            _udpClient = new UdpClientWrapper();
            _udpClient.OnMessage += HandleUdpMessage;
            await _udpClient.ConnectAsync(udpServer);

            await RunUdpHandshake(isReauth: false);
        }
        catch (Exception ex)
        {
            _udpReadyTcs.TrySetException(ex);
        }
    }

    private async Task RunUdpHandshake(bool isReauth)
    {
        _udpAuthKeyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _udpHandshakeCts = new CancellationTokenSource();

        _ = SendRawTcp("request_udp_auth", Array.Empty<byte>());

        string key;
        try
        {
            key = await _udpAuthKeyTcs.Task;
        }
        catch (TaskCanceledException)
        {
            if (!isReauth)
                _udpReadyTcs.TrySetException(new ConnectionFailedException("UDP authentication failed"));
            return;
        }

        var keyBytes = ByteString.CopyFrom(Encoding.UTF8.GetBytes(key));

        for (int i = 0; i < 5; i++)
        {
            if (_udpHandshakeCts.IsCancellationRequested) return;

            var envelope = new MessageEnvelope { Subject = "__ping__", SessionKey = keyBytes };
            try
            {
                await _udpClient!.SendAsync(envelope.ToByteArray());
            }
            catch { }

            await Task.Delay(500);
            if (_udpReadyTcs.Task.IsCompleted) break;
        }

        if (!_udpReadyTcs.Task.IsCompleted)
        {
            if (!isReauth)
                _udpReadyTcs.TrySetException(new ConnectionFailedException("UDP handshake timed out after 5 retries"));
            FireWarning("W002", "UDP handshake thất bại sau 5 lần thử");
            return;
        }

        _udpPing?.Dispose();
        _udpPing = new UdpPingService(_udpClient!, _udpPingIntervalMs, _udpPingTimeoutMs);
        _udpPing.OnPingTimeout += OnUdpPingTimeout;
        _udpPing.Start();
    }

    private async Task SendRawTcp(string subject, byte[] payload)
    {
        var envelope = new MessageEnvelope
        {
            Subject = subject,
            Payload = payload.Length > 0 ? ByteString.CopyFrom(payload) : ByteString.Empty
        };
        await _sendLock.WaitAsync();
        try
        {
            await _ws!.SendAsync(envelope.ToByteArray());
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async void OnUdpPingTimeout()
    {
        try
        {
            _udpReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await RunUdpHandshake(isReauth: true);
        }
        catch { }
        FireWarning("W001", "UDP ping timeout, kết nối có thể đã mất");
    }

    private void HandleUdpMessage(byte[] data)
    {
        try
        {
            var envelope = MessageEnvelope.Parser.ParseFrom(data);

            if (envelope.Subject == "__pong__")
            {
                _udpPing?.OnPongReceived(envelope.Ticks);
                return;
            }

            _udpDispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
        }
        catch { }
    }

    protected override void AutoPingWebSocketAndUdpThread()
    {
    }

    public override bool IsConnected => _ws?.IsConnected == true;

    public override async Task<IUser> Login<TData>(Func<TData> data)
    {
        return await LoginImpl<TData>(() => Task.FromResult(data()));
    }

    public override async Task<IUser> Login<TData>(Func<Task<TData>> data)
    {
        return await LoginImpl(data);
    }

    private async Task<IUser> LoginImpl<TData>(Func<Task<TData>> dataAsync) where TData : IMessage<TData>
    {
        var tcpServer = _config?.tcpServer ?? "127.0.0.1:9090";
        var restEndpoint = _config?.restEndpoint ?? "/api";
        var tcpSecurity = _config?.tcpSecurity ?? false;
        var protocol = tcpSecurity ? "https" : "http";

        _http ??= new HttpClient
        {
            BaseAddress = new Uri($"{protocol}://{tcpServer}{restEndpoint}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        var loginData = await dataAsync();
        var payload = ProtoSerializer.Serialize(loginData);

        var loginRequest = new ApiRequest
        {
            Subject = "__login__",
            Payload = ByteString.CopyFrom(payload),
            HasPayload = true
        };

        var response = await SendApiRequestExact(loginRequest);
        if (!response.Success)
            throw new ApiException(response.ErrorCode, response.ErrorMessage);

        var loginResult = LoginResponse.Parser.ParseFrom(response.Payload);

        _token = loginResult.Token;

        _reLoginFactory = async () =>
        {
            var reData = await dataAsync();
            var rePayload = ProtoSerializer.Serialize(reData);
            var reRequest = new ApiRequest
            {
                Subject = "__login__",
                Payload = ByteString.CopyFrom(rePayload),
                HasPayload = true
            };
            var reResponse = await SendApiRequestExact(reRequest);
            if (!reResponse.Success)
                throw new ApiException(reResponse.ErrorCode, reResponse.ErrorMessage);
            var reResult = LoginResponse.Parser.ParseFrom(reResponse.Payload);
            return ProtoSerializer.Serialize(reResult);
        };

        _loginDataFactory = dataAsync;

        return new UserInfo(loginResult.UserId, loginResult.UserName);
    }

    public override async Task<TResponse> GetRequest<TResponse>(string subject)
    {
        var request = new ApiRequest
        {
            Subject = subject,
            Token = _token ?? "",
            HasPayload = false
        };

        var response = await SendApiRequestWithRetry(request);
        return ProtoSerializer.Deserialize<TResponse>(response.Payload.ToByteArray());
    }

    public override async Task<TResponse> PostRequest<TRequest, TResponse>(string subject, TRequest body)
    {
        var payload = ProtoSerializer.Serialize(body);
        var compressedPayload = payload;
        var compressed = false;

        if (_config?.restCompressedEnable == true)
        {
            compressedPayload = Compress(payload);
            compressed = true;
        }

        var request = new ApiRequest
        {
            Subject = subject,
            Payload = ByteString.CopyFrom(compressedPayload),
            Token = _token ?? "",
            HasPayload = true,
            Compressed = compressed
        };

        var response = await SendApiRequestWithRetry(request);
        return ProtoSerializer.Deserialize<TResponse>(response.Payload.ToByteArray());
    }

    private async Task<ApiResponse> SendApiRequestWithRetry(ApiRequest request)
    {
        var response = await SendApiRequestExact(request);
        if (!response.Success && response.ErrorCode == "TokenExpired" && _reLoginFactory is not null)
        {
            var loginPayloadBytes = await _reLoginFactory();
            var loginResponse = LoginResponse.Parser.ParseFrom(loginPayloadBytes);
            _token = loginResponse.Token;

            request.Token = _token;
            response = await SendApiRequestExact(request);
        }
        if (!response.Success)
            throw new ApiException(response.ErrorCode, response.ErrorMessage);
        return response;
    }

    private async Task<ApiResponse> SendApiRequestExact(ApiRequest request)
    {
        var requestBytes = request.ToByteArray();
        using var content = new ByteArrayContent(requestBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var httpResponse = await _http!.PostAsync("", content);
        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync();
        return ApiResponse.Parser.ParseFrom(responseBytes);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    public override async Task DisconnectAsync()
    {
        _udpPing?.Dispose();
        _udpHandshakeCts?.Cancel();

        if (_udpClient != null)
        {
            try { await _udpClient.CloseAsync(); } catch { }
        }

        if (_ws != null)
        {
            try
            {
                await _ws.CloseAsync();
            }
            catch { }
        }

        if (_connectTask != null)
        {
            try { await _connectTask; } catch { }
        }

        _http?.Dispose();
        _http = null;
    }

    public override async Task Logout()
    {
        _token = null;
        _reLoginFactory = null;
        _loginDataFactory = null;
        await DisconnectAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await Logout();

        if (_udpClient != null)
            await _udpClient.DisposeAsync();

        _sendLock.Dispose();
    }

    public override long? LatestRttMs => _udpPing?.LatestRttMs;

    public override ISubscribe OnDisconnect(Action onDisconnect)
    {
        lock (_gate) { _onDisconnectCallbacks.Add(onDisconnect); }
        return new UnsubscribeHandle(() =>
        {
            lock (_gate) { _onDisconnectCallbacks.Remove(onDisconnect); }
        });
    }

    public override ISubscribe OnWarning(Action<WarningInfo> onWarning)
    {
        lock (_gate) { _onWarningCallbacks.Add(onWarning); }
        return new UnsubscribeHandle(() =>
        {
            lock (_gate) { _onWarningCallbacks.Remove(onWarning); }
        });
    }

    private void FireWarning(string code, string message, Exception? ex = null)
    {
        var info = new WarningInfo(code, message, ex);
        Action<WarningInfo>[] snapshot;
        lock (_gate) { snapshot = _onWarningCallbacks.ToArray(); }
        foreach (var cb in snapshot) cb(info);
    }

    public override async void SendOnTcp<TData>(string subject, TData data)
    {
        if (_ws == null || !_ws.IsConnected)
        {
            FireWarning("W003", "Gửi TCP thất bại, WebSocket chưa kết nối");
            return;
        }

        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope
        {
            Subject = subject,
            Payload = ByteString.CopyFrom(payload)
        };
        await _sendLock.WaitAsync();
        try
        {
            await _ws!.SendAsync(envelope.ToByteArray());
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override async void SendOnUdp<TData>(string subject, TData data)
    {
        await _udpReadyTcs.Task;

        if (_udpClient == null)
        {
            FireWarning("W004", "Gửi UDP thất bại");
            return;
        }

        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope
        {
            Subject = subject,
            Payload = ByteString.CopyFrom(payload)
        };
        try
        {
            await _udpClient.SendAsync(envelope.ToByteArray());
        }
        catch (Exception ex)
        {
            FireWarning("W004", "Gửi UDP thất bại", ex);
        }
    }

    public override ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data)
        => _tcpDispatcher.Subscribe(subject, data);

    public override ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data)
        => _udpDispatcher.Subscribe(subject, data);

    private class UnsubscribeHandle : ISubscribe
    {
        private readonly Action _action;
        public UnsubscribeHandle(Action action) => _action = action;
        public void UnSubscribe() => _action();
    }

    private class UserInfo : IUser
    {
        public string Name { get; }
        public string Id { get; }
        public UserInfo(string id, string name) { Id = id; Name = name; }
    }
}
