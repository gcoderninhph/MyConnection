namespace MyConnection
{
    public interface IServer : IAsyncDisposable
    {
        public static IServer Create(ServerConfig config)
        {
            throw new NotImplementedException();
        }
        IReadOnlyCollection<IConnection>  Connections { get; }
        string CreateToken(string id, string name);
        IConnection GetConnectionById(string id);
        ISubscribe OnConnect(Action<IConnection> onConnect);
        ISubscribe OnDisconnect(Action<IConnection> onDisconnect);
        void SendOnUdp<TData>(string subject, IConnection connection, TData data);
        void SendOnTcp<TData>(string subject, IConnection connection, TData data);
        void SendAllOnUdp<TData>(string subject, TData data);
        void SendAllOnTcp<TData>(string subject, TData data);
        ISubscribe SubscribeUdp<TData>(string subject, Action<IConnection, TData> data);
        ISubscribe SubscribeTcp<TData>(string subject, Action<IConnection, TData> data);
    }
}