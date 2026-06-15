#if NET9_0
using Google.Protobuf;
using System.IO.Compression;

namespace MyConnection;

public class ServerImplement : ServerAbstract
{
    private readonly ServerConfig _config;
    private readonly ServerTokenService _tokenService;
    private readonly ConnectionRegistry _registry;
    private readonly WebSocketListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly UdpSessionMap _sessionMap;
    private readonly UdpHandshakeHandler _handshakeHandler;
    private UdpListener? _udpListener;

    private Func<byte[], Task<byte[]>>? _loginHandler;
    private readonly Dictionary<string, Func<byte[], Task<byte[]>>> _getHandlers = new();
    private readonly Dictionary<string, Func<byte[], Task<byte[]>>> _postHandlers = new();

    private ServerImplement(ServerConfig config)
    {
        _config = config;
        _cts = new CancellationTokenSource();
        _tokenService = new ServerTokenService(config);
        _registry = new ConnectionRegistry();
        _sessionMap = new UdpSessionMap();
        _registry._sessionMap = _sessionMap;
        _handshakeHandler = new UdpHandshakeHandler(_sessionMap);
        _registry.SubscribeRawTcp("request_udp_auth", _handshakeHandler.OnUdpAuthRequest);
        _listener = new WebSocketListener(config, _tokenService, _registry, HandleRestRequest);
    }

    public static IServer Create(ServerConfig config)
    {
        var server = new ServerImplement(config);
        _ = server._listener.StartAsync(server._cts.Token);
        server._udpListener = new UdpListener(server._sessionMap, server._registry);
        _ = server._udpListener.StartAsync(config.udpPort, server._cts.Token);
        return server;
    }

    public override IReadOnlyCollection<IConnection> Connections
        => _registry.GetAll();

    public override ISubscribe OnConnect(Action<IConnection> callback)
        => _registry.OnConnect(callback);

    public override ISubscribe OnDisconnect(Action<IConnection> callback)
        => _registry.OnDisconnect(callback);

    public override string CreateToken(string id, string name)
        => _tokenService.CreateToken(id, name);

    public override IConnection GetConnectionById(string id)
        => _registry.GetById(id) ?? throw new KeyNotFoundException($"Connection {id} not found");

    public override async void SendOnUdp<TData>(string subject, IConnection connection, TData data)
    {
        try
        {
            var payload = ProtoSerializer.Serialize(data);
            var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
            await _udpListener!.SendTo(connection.Id, envelope.ToByteArray());
        }
        catch (InvalidOperationException)
        {
            _registry.FireWarning("W001", $"Gửi UDP thất bại, không có endpoint cho kết nối {connection.Id}", connection);
        }
        catch (Exception ex)
        {
            _registry.FireWarning("W002", $"Gửi UDP đến {connection.Id} thất bại", connection, ex);
        }
    }

    public override async void SendOnTcp<TData>(string subject, IConnection connection, TData data)
    {
        try
        {
            var payload = ProtoSerializer.Serialize(data);
            var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
            var conn = (ConnectionImplement)connection;
            await conn.SendAsync(envelope.ToByteArray());
        }
        catch (Exception ex)
        {
            _registry.FireWarning("W003", $"Gửi TCP đến {connection.Id} thất bại", connection, ex);
        }
    }

    public override async void SendAllOnUdp<TData>(string subject, TData data)
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
        var bytes = envelope.ToByteArray();
        foreach (var conn in _registry.GetAll())
        {
            if (!string.IsNullOrEmpty(conn.UdpAddress))
                await _udpListener!.SendTo(conn.Id, bytes);
        }
    }

    public override async void SendAllOnTcp<TData>(string subject, TData data)
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
        var bytes = envelope.ToByteArray();
        foreach (var conn in _registry.GetAll())
        {
            if (conn.Connected)
                await ((ConnectionImplement)conn).SendAsync(bytes);
        }
    }

    public override ISubscribe SubscribeUdp<TData>(string subject, Action<IConnection, TData> data)
        => _registry.SubscribeUdpLocal(subject, data);

    public override ISubscribe SubscribeTcp<TData>(string subject, Action<IConnection, TData> callback)
        => _registry.SubscribeTcpLocal(subject, callback);

    public override ISubscribe OnWarning(Action<ServerWarningInfo> onWarning)
        => _registry.OnWarning(onWarning);

    public override void OnLogin<TData>(Func<TData, Task<IUser>> authLogic)
    {
        _loginHandler = async (payload) =>
        {
            var data = ProtoSerializer.Deserialize<TData>(payload);
            var user = await authLogic(data);
            var token = _tokenService.CreateToken(user.Id, user.Name);
            var loginResponse = new LoginResponse
            {
                Token = token,
                UserId = user.Id,
                UserName = user.Name
            };
            return loginResponse.ToByteArray();
        };
    }

    public override void OnGetRequest<TResponse>(string subject, Func<Task<TResponse>> requestLogic)
    {
        _getHandlers[subject] = async (_) =>
        {
            var result = await requestLogic();
            return ProtoSerializer.Serialize(result);
        };
    }

    public override void OnPostRequest<TRequest, TResponse>(string subject, Func<TRequest, Task<TResponse>> requestLogic)
    {
        _postHandlers[subject] = async (payload) =>
        {
            var request = ProtoSerializer.Deserialize<TRequest>(payload);
            var result = await requestLogic(request);
            return ProtoSerializer.Serialize(result);
        };
    }

    private async Task<ApiResponse> HandleRestRequest(ApiRequest request)
    {
        try
        {
            if (request.Subject != "__login__")
            {
                var principal = _tokenService.ValidateToken(request.Token);
                if (principal is null)
                    return new ApiResponse { Success = false, ErrorCode = "TokenExpired", ErrorMessage = "Token không hợp lệ hoặc đã hết hạn" };
            }

            byte[] payload = request.Payload.ToByteArray();
            if (request.Compressed)
            {
                payload = Decompress(payload);
            }

            Func<byte[], Task<byte[]>>? handler = null;
            if (request.Subject == "__login__" && request.HasPayload)
            {
                handler = _loginHandler;
            }
            else if (!request.HasPayload)
            {
                _getHandlers.TryGetValue(request.Subject ?? "", out handler);
            }
            else
            {
                _postHandlers.TryGetValue(request.Subject ?? "", out handler);
            }

            if (handler is null)
                return new ApiResponse { Success = false, ErrorCode = "NotFound", ErrorMessage = $"Không tìm thấy handler cho subject '{request.Subject}'" };

            var result = await handler(payload);
            return new ApiResponse
            {
                Subject = request.Subject,
                Payload = ByteString.CopyFrom(result),
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new ApiResponse { Success = false, ErrorCode = "InternalError", ErrorMessage = ex.Message };
        }
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    public int WebSocketPort => _listener.Port;

    public void SubscribeConnection(string connectionId, string subject)
        => _registry.SubscribeConnection(connectionId, subject);

    public void UnsubscribeConnection(string connectionId, string subject)
        => _registry.UnsubscribeConnection(connectionId, subject);

    public override async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udpListener?.StopAsync();
        await _listener.StopAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
#endif
