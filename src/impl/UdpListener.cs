#if NET9_0
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

namespace MyConnection;

public class UdpListener
{
    private readonly UdpSessionMap _sessionMap;
    private readonly ConnectionRegistry _registry;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

    public UdpListener(UdpSessionMap sessionMap, ConnectionRegistry registry)
    {
        _sessionMap = sessionMap;
        _registry = registry;
    }

    public Task StartAsync(int port, CancellationToken ct)
    {
        _udpClient = new UdpClient(port);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReceiveLoop();
        return Task.CompletedTask;
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (_cts is not null && !_cts.IsCancellationRequested)
            {
                var result = await _udpClient!.ReceiveAsync(_cts.Token);
                _ = HandleDatagram(result.RemoteEndPoint, result.Buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleDatagram(IPEndPoint remoteEP, byte[] data)
    {
        try
        {
            var envelope = MessageEnvelope.Parser.ParseFrom(data);

            if (envelope.Subject == "__ping__")
            {
                var pongEnvelope = new MessageEnvelope
                {
                    Subject = "__pong__",
                    Ticks = envelope.Ticks
                };
                var pongBytes = pongEnvelope.ToByteArray();
                try { await _udpClient!.SendAsync(pongBytes, pongBytes.Length, remoteEP); } catch { }

                if (envelope.SessionKey.Length != 0)
                {
                    ProcessSessionKey(remoteEP, envelope);
                    return;
                }

                UpdatePingTime(remoteEP);
                RouteData(remoteEP, envelope);
                return;
            }

            if (envelope.SessionKey.Length != 0)
            {
                ProcessSessionKey(remoteEP, envelope);
                return;
            }

            RouteData(remoteEP, envelope);
        }
        catch { }
    }

    private void ProcessSessionKey(IPEndPoint remoteEP, MessageEnvelope envelope)
    {
        var keyHex = System.Text.Encoding.UTF8.GetString(envelope.SessionKey.ToByteArray());

        if (_sessionMap.IsAlreadyBound(keyHex))
        {
            var connId = _sessionMap.GetConnectionId(remoteEP);
            if (connId is not null)
                SendUdpBoundAck(connId);
            RouteData(remoteEP, envelope);
            return;
        }

        if (_sessionMap.BindEndpoint(remoteEP, keyHex))
        {
            var connId = _sessionMap.GetConnectionId(remoteEP);
            if (connId is not null)
            {
                _registry.BindUdp(connId, remoteEP.ToString());
                SendUdpBoundAck(connId);
            }
            RouteData(remoteEP, envelope);
        }
    }

    private async void SendUdpBoundAck(string connectionId)
    {
        var conn = _registry.GetById(connectionId);
        if (conn is null) return;

        var envelope = new MessageEnvelope { Subject = "__udp_bound__" };
        try { await conn.SendAsync(envelope.ToByteArray()); } catch { }
    }

    private void UpdatePingTime(IPEndPoint remoteEP)
    {
        var connId = _sessionMap.GetConnectionId(remoteEP);
        if (connId is null) return;
        var conn = _registry.GetById(connId);
        if (conn is not null)
            conn.UdpPingTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void RouteData(IPEndPoint remoteEP, MessageEnvelope envelope)
    {
        if (envelope.Subject == "__ping__") return;
        var connId = _sessionMap.GetConnectionId(remoteEP);
        if (connId is null) return;
        _registry.Route(connId, envelope.Subject, envelope.Payload.ToByteArray(), fromUdp: true);
    }

    public async Task SendTo(string connectionId, byte[] data)
    {
        var ep = _sessionMap.GetEndpoint(connectionId);
        if (ep is null)
            throw new InvalidOperationException($"No UDP endpoint bound for connection {connectionId}");
        await _udpClient!.SendAsync(data, data.Length, ep);
    }

    public void StopAsync()
    {
        _cts?.Cancel();
        try { _udpClient?.Close(); } catch { }
        _udpClient?.Dispose();
    }
}
#endif
