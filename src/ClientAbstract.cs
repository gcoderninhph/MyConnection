using Google.Protobuf;

namespace MyConnection
{
    public abstract class ClientAbstract : IClient
    {
        protected ClientConfig? _config;
        protected string? _token;

        public async Task ConnectServer()
        {
            if (_config is null)
                throw new InvalidOperationException("ClientConfig not set. Call IClient.Create(config) first.");

            var protocol = _config.tcpSecurity ? "wss" : "ws";
            var fullWsUrl = $"{protocol}://{_config.tcpServer}{_config.websocketEnpoint}";
            await ConnectWebSocket(_token ?? "", fullWsUrl);
            NotifyConnectUdp(_token ?? "", _config.udpServer);
            AutoPingWebSocketAndUdpThread();
        }

        public abstract Task DisconnectAsync();
        public abstract Task Logout();

        public virtual async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }

        protected abstract Task ConnectWebSocket(string token, string websocketServer);

        protected abstract void NotifyConnectUdp(string token, string udpServer);

        protected abstract void AutoPingWebSocketAndUdpThread();

        public abstract void SendOnUdp<TData>(string subject, TData data) where TData : IMessage<TData>;
        public abstract void SendOnTcp<TData>(string subject, TData data) where TData : IMessage<TData>;

        public abstract ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data) where TData : IMessage<TData>;
        public abstract ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data) where TData : IMessage<TData>;
        public abstract ISubscribe OnDisconnect(Action onDisconnect);
        public abstract ISubscribe OnWarning(Action<WarningInfo> onWarning);
        public abstract long? LatestRttMs { get; }

        public abstract bool IsConnected { get; }
        public abstract Task<IUser> Login<TData>(Func<TData> data) where TData : IMessage<TData>;
        public abstract Task<IUser> Login<TData>(Func<Task<TData>> data) where TData : IMessage<TData>;
        public abstract Task<TResponse> GetRequest<TResponse>(string subject) where TResponse : IMessage<TResponse>;
        public abstract Task<TResponse> PostRequest<TRequest, TResponse>(string subject, TRequest body) where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>;
    }
}
