namespace MyConnection
{
    public interface IClient
    {
        Task ConnectServer(ClientConfig config);
        void SendOnUdp<TData>(string subject, TData data);
        void SendOnTcp<TData>(string subject, TData data);
        ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data);
        ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data);
        ISubscribe OnDisconnect(Action onDisconnect);
    }
}
