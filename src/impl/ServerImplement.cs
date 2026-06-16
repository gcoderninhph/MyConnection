#if NET9_0
namespace MyConnection;

public class ServerImplement : ServerCore
{
    private readonly WebSocketListener _listener;

    private ServerImplement(ServerConfig config) : base(config)
    {
        _listener = new WebSocketListener(config, _tokenService, _registry, HandleRestRequest);
    }

    public static IServer Create(ServerConfig config)
    {
        var server = new ServerImplement(config);
        _ = server.StartTransportAsync(server._cts.Token);
        server._udpListener = new UdpListener(server._sessionMap, server._registry);
        _ = server._udpListener.StartAsync(config.udpPort, server._cts.Token);
        return server;
    }

    protected override async ValueTask StartTransportAsync(CancellationToken ct)
    {
        await _listener.StartAsync(ct);
    }

    protected override ValueTask StopTransportAsync()
        => _listener.StopAsync();

    public int WebSocketPort => _listener.Port;

    public void SubscribeConnection(string connectionId, string subject)
        => _registry.SubscribeConnection(connectionId, subject);

    public void UnsubscribeConnection(string connectionId, string subject)
        => _registry.UnsubscribeConnection(connectionId, subject);
}
#endif
