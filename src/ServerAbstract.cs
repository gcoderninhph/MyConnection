using Google.Protobuf;

namespace MyConnection
{
    public abstract class ServerAbstract : IServer
    {
        public abstract IReadOnlyCollection<IConnection> Connections { get; }
        public abstract string CreateToken(string id, string name);
        public abstract IConnection GetConnectionById(string id);
        public abstract ISubscribe OnConnect(Action<IConnection> onConnect);
        public abstract ISubscribe OnDisconnect(Action<IConnection> onDisconnect);
        public abstract void SendOnUdp<TData>(string subject, IConnection connection, TData data) where TData : IMessage<TData>;
        public abstract void SendOnTcp<TData>(string subject, IConnection connection, TData data) where TData : IMessage<TData>;
        public abstract void SendAllOnUdp<TData>(string subject, TData data) where TData : IMessage<TData>;
        public abstract void SendAllOnTcp<TData>(string subject, TData data) where TData : IMessage<TData>;
        public abstract ISubscribe SubscribeUdp<TData>(string subject, Action<IConnection, TData> data) where TData : IMessage<TData>;
        public abstract ISubscribe SubscribeTcp<TData>(string subject, Action<IConnection, TData> data) where TData : IMessage<TData>;
        public abstract ISubscribe OnWarning(Action<ServerWarningInfo> onWarning);
        public abstract void OnLogin<TData>(Func<TData, Task<IUser>> authLogic) where TData : IMessage<TData>;
        public abstract void OnGetRequest<TResponse>(string subject, Func<IUser, Task<TResponse>> requestLogic) where TResponse : IMessage<TResponse>;
        public abstract void OnPostRequest<TRequest, TResponse>(string subject, Func<IUser, TRequest, Task<TResponse>> requestLogic) where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>;
        public abstract ValueTask DisposeAsync();
    }
}
