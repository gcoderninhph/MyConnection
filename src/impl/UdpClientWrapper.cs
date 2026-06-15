using System.Net;
using System.Net.Sockets;

namespace MyConnection;

public class UdpClientWrapper : IAsyncDisposable
{
    private UdpClient? _udpClient;
    private IPEndPoint? _serverEP;
    private CancellationTokenSource? _cts;

    public event Action<byte[]>? OnMessage;

    public Task ConnectAsync(string serverEndpoint)
    {
        var colonIndex = serverEndpoint.LastIndexOf(':');
        var host = colonIndex >= 0 ? serverEndpoint.Substring(0, colonIndex) : serverEndpoint;
        var port = colonIndex >= 0 ? int.Parse(serverEndpoint.Substring(colonIndex + 1)) : 9091;

        _serverEP = new IPEndPoint(IPAddress.Parse(host), port);
        _udpClient = new UdpClient();
        _udpClient.Connect(_serverEP);
        _cts = new CancellationTokenSource();

        _ = ReceiveLoop();
        return Task.CompletedTask;
    }

    public async Task SendAsync(byte[] data)
    {
        if (_udpClient is null) return;
        await _udpClient.SendAsync(data, data.Length);
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (_cts is not null && !_cts.IsCancellationRequested)
            {
#if NET9_0
                var result = await _udpClient!.ReceiveAsync(_cts.Token);
#else
                var result = await _udpClient!.ReceiveAsync();
#endif
                OnMessage?.Invoke(result.Buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public Task CloseAsync()
    {
        _cts?.Cancel();
        try { _udpClient?.Close(); } catch { }
        _udpClient?.Dispose();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _udpClient?.Dispose();
        return default;
    }
}
