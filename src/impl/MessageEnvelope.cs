using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace MyConnection;

public sealed class MessageEnvelope : IMessage<MessageEnvelope>
{
    private static readonly MessageParser<MessageEnvelope> _parser = new(() => new MessageEnvelope());

    public static MessageParser<MessageEnvelope> Parser => _parser;

    public string Subject { get; set; } = "";
    public ByteString Payload { get; set; } = ByteString.Empty;
    public ByteString SessionKey { get; set; } = ByteString.Empty;

    public MessageDescriptor Descriptor => null!;

    public MessageEnvelope() { }

    public MessageEnvelope(MessageEnvelope other) : this()
    {
        Subject = other.Subject;
        Payload = other.Payload;
        SessionKey = other.SessionKey;
    }

    public MessageEnvelope Clone() => new(this);

    public bool Equals(MessageEnvelope? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Subject == other.Subject && Payload == other.Payload && SessionKey == other.SessionKey;
    }

    public override bool Equals(object? obj) => Equals(obj as MessageEnvelope);
    public override int GetHashCode() => HashCode.Combine(Subject, Payload, SessionKey);

    public void MergeFrom(MessageEnvelope other)
    {
        Subject = other.Subject;
        Payload = other.Payload;
        SessionKey = other.SessionKey;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: Subject = input.ReadString(); break;
                case 2: Payload = input.ReadBytes(); break;
                case 3: SessionKey = input.ReadBytes(); break;
                default: input.SkipLastField(); break;
            }
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (Subject.Length != 0)
        {
            output.WriteRawTag(10);
            output.WriteString(Subject);
        }
        if (Payload.Length != 0)
        {
            output.WriteRawTag(18);
            output.WriteBytes(Payload);
        }
        if (SessionKey.Length != 0)
        {
            output.WriteRawTag(26);
            output.WriteBytes(SessionKey);
        }
    }

    public int CalculateSize()
    {
        int size = 0;
        if (Subject.Length != 0)
            size += 1 + CodedOutputStream.ComputeStringSize(Subject);
        if (Payload.Length != 0)
            size += 1 + CodedOutputStream.ComputeBytesSize(Payload);
        if (SessionKey.Length != 0)
            size += 1 + CodedOutputStream.ComputeBytesSize(SessionKey);
        return size;
    }
}
