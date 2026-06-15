using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace MyConnection;

public class ConnectionImplement : IConnection
{
    public string Id { get; }
    public IUser User { get; }
    public IDictionary<string, object> Attributes => _attributes;
    public bool Connected => _webSocket.State == WebSocketState.Open;
    public string UdpAddress { get; set; } = "";
    public string WebSocketSessionId { get; }
    public long UdpPingTime { get; set; }
    public long WebSocketPingTime { get; set; }

    internal readonly WebSocket _webSocket;
    internal readonly ConcurrentDictionary<string, object> _attributes = new();

    public ConnectionImplement(WebSocket webSocket, IUser user)
    {
        _webSocket = webSocket;
        User = user;
        Id = Guid.NewGuid().ToString("N");
        WebSocketSessionId = Guid.NewGuid().ToString("N");
    }

    internal async Task SendAsync(byte[] data)
    {
        await _webSocket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None);
    }

    internal async Task CloseAsync()
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Server closing",
                CancellationToken.None);
        }
    }
}
