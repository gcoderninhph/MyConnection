namespace MyConnection
{
    /// <summary>
    /// Cấu hình phía server.
    /// </summary>
    public class ServerConfig
    {
        /// <summary>
        /// Điểm cuối REST API, mặc định "/api".
        /// </summary>
        public string restEndpoint = "/api";

        /// <summary>
        /// Điểm cuối WebSocket, mặc định "/ws".
        /// </summary>
        public string websocketEndpoint = "/ws";

        /// <summary>
        /// Bật hỗ trợ giải nén Deflate payload từ POST request của client. Mặc định false.
        /// </summary>
        public bool restCompressedEnable = false;

        /// <summary>
        /// Cổng UDP để nhận message. Mặc định 9091.
        /// </summary>
        public int udpPort = 9091;

        /// <summary>
        /// Cổng TCP để lắng nghe HTTP/WebSocket. Mặc định 9090.
        /// </summary>
        public int tcpPort = 9090;

        /// <summary>
        /// Khóa bí mật dùng để ký và xác minh JWT token.
        /// </summary>
        public string jwtSecret = "";

        /// <summary>
        /// Giá trị "aud" (Audience) trong JWT token.
        /// </summary>
        public string jwtAudience = "";

        /// <summary>
        /// Giá trị "iss" (Issuer) trong JWT token.
        /// </summary>
        public string jwtIssuer = "";
    }
}