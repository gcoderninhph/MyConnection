using Google.Protobuf;

namespace MyConnection
{
    public interface IClientModule
    {
        void SetIClient(IClient client);
    }
}