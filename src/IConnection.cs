namespace MyConnection
{
    public interface IConnection
    {
        string Id { get; }
        IUser User { get; }
        IDictionary<string, object> Attributes { get; }
        bool Connected { get; }
        string UdpAddress { get; }
        string WebSocketSessionId { get; }
        long UdpPingTime { get; }
        long WebSocketPingTime { get; }
    }
}