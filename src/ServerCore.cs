#if NET9_0
using Google.Protobuf;
using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net.WebSockets;

namespace MyConnection;

public abstract class ServerCore : ServerAbstract
{
    protected readonly ServerConfig _config;
    protected readonly ServerTokenService _tokenService;
    protected readonly ConnectionRegistry _registry;
    protected readonly CancellationTokenSource _cts;
    protected readonly UdpSessionMap _sessionMap;
    protected readonly UdpHandshakeHandler _handshakeHandler;
    protected UdpListener? _udpListener;

    protected Func<byte[], Task<byte[]>>? _loginHandler;
    protected readonly Dictionary<string, Func<IUser, byte[], Task<byte[]>>> _getHandlers = new();
    protected readonly Dictionary<string, Func<IUser, byte[], Task<byte[]>>> _postHandlers = new();

    protected ServerCore(ServerConfig config)
    {
        _config = config;
        _cts = new CancellationTokenSource();
        _tokenService = new ServerTokenService(config);
        _registry = new ConnectionRegistry();
        _sessionMap = new UdpSessionMap();
        _registry._sessionMap = _sessionMap;
        _handshakeHandler = new UdpHandshakeHandler(_sessionMap);
        _registry.SubscribeRawTcp("request_udp_auth", _handshakeHandler.OnUdpAuthRequest);
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

    public override void OnGetRequest<TResponse>(string subject, Func<IUser, Task<TResponse>> requestLogic)
    {
        _getHandlers[subject] = async (currentUser, _) =>
        {
            var result = await requestLogic(currentUser);
            return ProtoSerializer.Serialize(result);
        };
    }

    public override void OnPostRequest<TRequest, TResponse>(string subject, Func<IUser, TRequest, Task<TResponse>> requestLogic)
    {
        _postHandlers[subject] = async (currentUser, payload) =>
        {
            var request = ProtoSerializer.Deserialize<TRequest>(payload);
            var result = await requestLogic(currentUser, request);
            return ProtoSerializer.Serialize(result);
        };
    }

    protected async Task<ApiResponse> HandleRestRequest(ApiRequest request)
    {
        try
        {
            IUser? currentUser = null;
            if (request.Subject != "__login__")
            {
                var principal = _tokenService.ValidateToken(request.Token);
                if (principal is null)
                    return new ApiResponse { Success = false, ErrorCode = "TokenExpired", ErrorMessage = "Token không hợp lệ hoặc đã hết hạn" };

                var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "";
                var userName = principal.FindFirst(JwtRegisteredClaimNames.Name)?.Value ?? "";
                currentUser = new JwtUser(userId, userName);
            }

            byte[] payload = request.Payload.ToByteArray();
            if (request.Compressed)
                payload = Decompress(payload);

            byte[] result;
            if (request.Subject == "__login__" && request.HasPayload)
            {
                if (_loginHandler is null)
                    return new ApiResponse { Success = false, ErrorCode = "NotFound", ErrorMessage = $"Không tìm thấy handler cho subject '{request.Subject}'" };

                result = await _loginHandler(payload);
            }
            else
            {
                Func<IUser, byte[], Task<byte[]>>? handler = null;
                if (!request.HasPayload)
                    _getHandlers.TryGetValue(request.Subject ?? "", out handler);
                else
                    _postHandlers.TryGetValue(request.Subject ?? "", out handler);

                if (handler is null)
                    return new ApiResponse { Success = false, ErrorCode = "NotFound", ErrorMessage = $"Không tìm thấy handler cho subject '{request.Subject}'" };

                result = await handler(currentUser!, payload);
            }

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

    protected static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    protected async Task HandleWebSocketConnection(WebSocket ws, IUser user, CancellationToken ct)
    {
        var connection = new ConnectionImplement(ws, user);
        _registry.Register(connection);

        await ReceiveLoop(connection, ws, _registry, ct);

        await connection.CloseAsync();
        _registry.Remove(connection.Id);
    }

    internal static async Task ReceiveLoop(ConnectionImplement connection, WebSocket webSocket, ConnectionRegistry registry, CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (webSocket.State == WebSocketState.Open && !linkedCts.Token.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();
            try
            {
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedCts.Token);
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            connection.WebSocketPingTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var data = ms.ToArray();
            try
            {
                var envelope = MessageEnvelope.Parser.ParseFrom(data);
                registry.Route(connection.Id, envelope.Subject, envelope.Payload.ToByteArray(), fromUdp: false);
            }
            catch
            {
            }
        }
    }

    protected abstract ValueTask StartTransportAsync(CancellationToken ct);

    protected abstract ValueTask StopTransportAsync();

    public override async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udpListener?.StopAsync();
        await StopTransportAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal class JwtUser : IUser
{
    public string Name { get; }
    public string Id { get; }
    public JwtUser(string id, string name) { Id = id; Name = name; }
}
#endif
