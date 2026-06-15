namespace MyConnection
{
    /// <summary>
    /// Cấu hình kết nối phía client.
    /// </summary>
    public class ClientConfig
    {
        /// <summary>
        /// Địa chỉ TCP server (WebSocket), định dạng "host:port". Mặc định "127.0.0.1:9090".
        /// </summary>
        public string tcpServer = "127.0.0.1:9090";

        /// <summary>
        /// Điểm cuối WebSocket trên server, mặc định "/ws".
        /// </summary>
        public string websocketEnpoint = "/ws";

        /// <summary>
        /// Điểm cuối REST API trên server, mặc định "/api".
        /// </summary>
        public string restEndpoint = "/api";

        /// <summary>
        /// Bật chế độ nén Deflate payload của POST request trước khi gửi lên server. Mặc định false.
        /// </summary>
        public bool restCompressedEnable = false;

        /// <summary>
        /// Bật giao thức bảo mật TLS: nếu true, REST dùng https:// và WebSocket dùng wss://. Mặc định false.
        /// </summary>
        public bool tcpSecurity = false;

        /// <summary>
        /// Địa chỉ UDP server, định dạng "host:port". Để trống nếu không dùng UDP. Mặc định "127.0.0.1:9091".
        /// </summary>
        public string udpServer  = "127.0.0.1:9091";

        /// <summary>
        /// Chu kỳ ping UDP (millisecond) để đo RTT và giữ kết nối. Mặc định 5000.
        /// </summary>
        public int udpPingIntervalMs = 5000;

        /// <summary>
        /// Thời gian chờ ping UDP (millisecond) trước khi coi là timeout. Mặc định 15000.
        /// </summary>
        public int udpPingTimeoutMs = 15000;
    }
}