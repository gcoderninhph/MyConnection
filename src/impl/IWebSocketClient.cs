namespace MyConnection;

internal interface IWebSocketClient
{
    bool IsConnected { get; }

    event Action? OnOpen;
    event Action<string>? OnError;
    event Action<byte[]>? OnMessage;
    event Action<int>? OnClose;

    Task ConnectAsync();
    Task SendAsync(byte[] data);
    Task CloseAsync();
    void CancelConnection();
}
