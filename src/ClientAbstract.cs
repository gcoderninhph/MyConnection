namespace MyConnection
{
    public abstract class ClientAbstract : IClient
    {
        public async Task ConnectServer(ClientConfig config)
        {
            await ConnectWebSocket(config.token, config.websocketServer);
            NotifyConnectUdp(config.token, config.udpServer);
            AutoPingWebSocketAndUdpThread();
        }

        protected abstract Task ConnectWebSocket(string token, string websocketServer);

        protected abstract void NotifyConnectUdp(string token, string udpServer);

        // Tự động ping lên server cả websocket và udp
        protected abstract void AutoPingWebSocketAndUdpThread();

        public abstract void SendOnUdp<TData>(string subject, TData data);
        public abstract void SendOnTcp<TData>(string subject, TData data);

        public abstract ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data);
        public abstract ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data);
        public abstract ISubscribe OnDisconnect(Action onDisconnect);
    }
}
