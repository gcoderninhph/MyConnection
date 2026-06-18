#if UNITY_ENGINE
using NativeWebSocket;

namespace MyConnection;

internal sealed class NativeWebSocketClient : IWebSocketClient
{
    private readonly WebSocket _ws;

    public bool IsConnected => _ws.State == WebSocketState.Open;

    public event Action? OnOpen;
    public event Action<string>? OnError;
    public event Action<byte[]>? OnMessage;
    public event Action<int>? OnClose;

    public NativeWebSocketClient(string url, Dictionary<string, string> headers)
    {
        _ws = new WebSocket(url, headers);
    }

    public Task ConnectAsync()
    {
        _ws.OnOpen += () => OnOpen?.Invoke();
        _ws.OnError += (err) => OnError?.Invoke(err);
        _ws.OnMessage += (data) => OnMessage?.Invoke(data);
        _ws.OnClose += (code) => OnClose?.Invoke(code);
        return _ws.Connect();
    }

    public Task SendAsync(byte[] data)
    {
        _ws.Send(data);
        return Task.CompletedTask;
    }

    public async Task CloseAsync()
    {
        await _ws.Close();
    }

    public void CancelConnection()
    {
        _ws.CancelConnection();
    }
}
#endif
