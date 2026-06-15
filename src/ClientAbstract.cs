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

        public abstract Task DisconnectAsync();

        public virtual async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }

        protected abstract Task ConnectWebSocket(string token, string websocketServer);

        protected abstract void NotifyConnectUdp(string token, string udpServer);

        protected abstract void AutoPingWebSocketAndUdpThread();

        public abstract void SendOnUdp<TData>(string subject, TData data);
        public abstract void SendOnTcp<TData>(string subject, TData data);

        public abstract ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data);
        public abstract ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data);
        public abstract ISubscribe OnDisconnect(Action onDisconnect);
        public abstract ISubscribe OnWarning(Action<WarningInfo> onWarning);
        public abstract long? LatestRttMs { get; }
    }
}
