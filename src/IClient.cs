namespace MyConnection
{
    public interface IClient : IAsyncDisposable
    {
        public static IClient Create()
        {
            return new ClientImplement();
        }

        Task ConnectServer(ClientConfig config);
        Task DisconnectAsync();
        void SendOnUdp<TData>(string subject, TData data);
        void SendOnTcp<TData>(string subject, TData data);
        ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data);
        ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data);
        ISubscribe OnDisconnect(Action onDisconnect);
    }
}
