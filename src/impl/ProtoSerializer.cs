using Google.Protobuf;

namespace MyConnection;

public static class ProtoSerializer
{
    public static byte[] Serialize<T>(T message) where T : IMessage<T>
    {
        return message.ToByteArray();
    }

    public static T Deserialize<T>(byte[] data) where T : IMessage<T>
    {
        return new MessageParser<T>(() => (T)Activator.CreateInstance(typeof(T))!).ParseFrom(data);
    }
}
