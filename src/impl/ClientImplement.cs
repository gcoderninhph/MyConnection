using Google.Protobuf;
using NativeWebSocket;

namespace MyConnection;

public class ClientImplement : ClientAbstract
{
    private WebSocket? _ws;
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

    protected override async Task ConnectWebSocket(string token, string websocketServer)
    {
        _udpReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _tcpDispatcher.OnEmptyDispatch += sub => FireWarning("W006", $"Tin nhắn TCP bị rơi, không có subscriber cho subject '{sub}'");
        _udpDispatcher.OnEmptyDispatch += sub => FireWarning("W007", $"Tin nhắn UDP bị rơi, không có subscriber cho subject '{sub}'");

        var uri = new Uri(
            websocketServer.Contains("://") ? websocketServer : "ws://" + websocketServer);

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token
        };
        _ws = new WebSocket(uri.ToString(), headers);

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

        _connectTask = _ws.Connect();

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
                var key = System.Text.Encoding.UTF8.GetString(envelope.Payload.ToByteArray());
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

        _udpPingIntervalMs = _udpPingIntervalMs > 0 ? _udpPingIntervalMs : 5000;
        _udpPingTimeoutMs = _udpPingTimeoutMs > 0 ? _udpPingTimeoutMs : 15000;

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

        var keyBytes = ByteString.CopyFrom(System.Text.Encoding.UTF8.GetBytes(key));

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
            await _ws!.Send(envelope.ToByteArray());
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
                await _ws.Close();
            }
            catch { }
        }

        if (_connectTask != null)
        {
            try { await _connectTask; } catch { }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

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
        if (_ws == null || _ws.State != WebSocketState.Open)
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
            await _ws!.Send(envelope.ToByteArray());
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
}
