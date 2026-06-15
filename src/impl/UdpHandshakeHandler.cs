#if NET9_0
using Google.Protobuf;

namespace MyConnection;

public class UdpHandshakeHandler
{
    private readonly UdpSessionMap _sessionMap;

    public UdpHandshakeHandler(UdpSessionMap sessionMap)
    {
        _sessionMap = sessionMap;
    }

    public async void OnUdpAuthRequest(IConnection connection, byte[] payload)
    {
        var key = _sessionMap.GenerateKey();
        _sessionMap.Invalidate(connection.Id);
        _sessionMap.RegisterKey(key, connection.Id);

        var envelope = new MessageEnvelope
        {
            Subject = "__udp_auth__",
            Payload = ByteString.CopyFrom(System.Text.Encoding.UTF8.GetBytes(key))
        };
        var conn = (ConnectionImplement)connection;
        await conn.SendAsync(envelope.ToByteArray());
    }
}
#endif
