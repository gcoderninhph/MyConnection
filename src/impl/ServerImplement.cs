#if NET9_0
using Google.Protobuf;

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
        _listener = new WebSocketListener(config, _tokenService, _registry);
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
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
        await _udpListener!.SendTo(connection.Id, envelope.ToByteArray());
    }

    public override async void SendOnTcp<TData>(string subject, IConnection connection, TData data)
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
        var conn = (ConnectionImplement)connection;
        await conn.SendAsync(envelope.ToByteArray());
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
