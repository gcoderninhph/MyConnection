namespace MyConnection
{
    public class ServerConfig
    {
        public string websocketEndpoint = "0.0.0.0:9090/ws";
        public int udpPort = 9091;
        public string jwtSecret = "";
        public string jwtAudience = "";
        public string jwtIssuer = "";
    }
}