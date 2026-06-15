using Google.Protobuf;

namespace MyConnection;

public class UdpPingService : IDisposable
{
    private readonly UdpClientWrapper _udpClient;
    private readonly int _intervalMs;
    private readonly int _timeoutMs;
    private Timer? _timer;
    private long _lastPongTime;

    public event Action? OnPingTimeout;

    public UdpPingService(UdpClientWrapper udpClient, int intervalMs, int timeoutMs)
    {
        _udpClient = udpClient;
        _intervalMs = intervalMs;
        _timeoutMs = timeoutMs;
    }

    public void Start()
    {
        _lastPongTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _timer = new Timer(OnTick, null, 0, _intervalMs);
    }

    private async void OnTick(object? _)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long elapsed = now - _lastPongTime;

        var envelope = new MessageEnvelope { Subject = "__ping__" };
        try
        {
            await _udpClient.SendAsync(envelope.ToByteArray());
        }
        catch { }

        if (elapsed > _timeoutMs)
            OnPingTimeout?.Invoke();
    }

    public void OnPongReceived()
    {
        _lastPongTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
