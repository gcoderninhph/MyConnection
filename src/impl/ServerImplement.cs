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

    private ServerImplement(ServerConfig config)
    {
        _config = config;
        _cts = new CancellationTokenSource();
        _tokenService = new ServerTokenService(config);
        _registry = new ConnectionRegistry();
        _listener = new WebSocketListener(config, _tokenService, _registry);
    }

    public static IServer Create(ServerConfig config)
    {
        var server = new ServerImplement(config);
        _ = server._listener.StartAsync(server._cts.Token);
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

    public override void SendOnUdp<TData>(string subject, IConnection connection, TData data)
        => throw new NotImplementedException("UDP not implemented yet");

    public override async void SendOnTcp<TData>(string subject, IConnection connection, TData data)
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
        var conn = (ConnectionImplement)connection;
        await conn.SendAsync(envelope.ToByteArray());
    }

    public override void SendAllOnUdp<TData>(string subject, TData data)
        => throw new NotImplementedException("UDP not implemented yet");

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
        => throw new NotImplementedException("UDP not implemented yet");

    public override ISubscribe SubscribeTcp<TData>(string subject, Action<IConnection, TData> callback)
        => _registry.SubscribeLocal(subject, callback);

    public int WebSocketPort => _listener.Port;

    public void SubscribeConnection(string connectionId, string subject)
        => _registry.SubscribeConnection(connectionId, subject);

    public void UnsubscribeConnection(string connectionId, string subject)
        => _registry.UnsubscribeConnection(connectionId, subject);

    public override async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _listener.StopAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
#endif
