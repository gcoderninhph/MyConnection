namespace MyConnection
{
    public class ClientConfig
    {
        public string token;
        public string websocketServer;
        public string udpServer;
        public int udpPingIntervalMs = 5000;
        public int udpPingTimeoutMs = 15000;
    }
}