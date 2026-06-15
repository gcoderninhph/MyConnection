namespace MyConnection
{
    public abstract class ServerAbstract : IServer
    {
        public abstract IReadOnlyCollection<IConnection> Connections { get; }
        public abstract string CreateToken(string id, string name);
        public abstract IConnection GetConnectionById(string id);
        public abstract ISubscribe OnConnect(Action<IConnection> onConnect);
        public abstract ISubscribe OnDisconnect(Action<IConnection> onDisconnect);
        public abstract void SendOnUdp<TData>(string subject, IConnection connection, TData data);
        public abstract void SendOnTcp<TData>(string subject, IConnection connection, TData data);
        public abstract void SendAllOnUdp<TData>(string subject, TData data);
        public abstract void SendAllOnTcp<TData>(string subject, TData data);
        public abstract ISubscribe SubscribeUdp<TData>(string subject, Action<IConnection, TData> data);
        public abstract ISubscribe SubscribeTcp<TData>(string subject, Action<IConnection, TData> data);
        public abstract ValueTask DisposeAsync();
    }
}