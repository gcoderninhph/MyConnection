using Google.Protobuf;

namespace MyConnection;

public static class ProtoSerializer
{
    public static byte[] Serialize<T>(T message)
    {
        var msg = (message as IMessage)
            ?? throw new InvalidOperationException(
                $"TData '{typeof(T).Name}' must implement Google.Protobuf.IMessage");
        return msg.ToByteArray();
    }

    public static T Deserialize<T>(byte[] data)
    {
        var instance = Activator.CreateInstance<T>();
        var msg = (instance as IMessage)
            ?? throw new InvalidOperationException(
                $"TData '{typeof(T).Name}' must implement Google.Protobuf.IMessage");
        msg.MergeFrom(new CodedInputStream(data));
        return instance;
    }
}
