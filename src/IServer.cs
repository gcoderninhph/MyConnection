using Google.Protobuf;

namespace MyConnection
{
    /// <summary>
    /// Giao diện phía server. Quản lý kết nối, nhận/gửi message qua TCP/UDP, xác thực JWT, và REST endpoint.
    /// Hai transport: <see cref="ServerConfig"/> (raw TcpListener) và <see cref="ServerKestrelConfig"/> (Kestrel).
    /// </summary>
    public interface IServer : IAsyncDisposable
    {
        /// <summary>
        /// Tạo server từ cấu hình. Chỉ hỗ trợ .NET 9.0.
        /// Truyền <see cref="ServerKestrelConfig"/> để dùng Kestrel transport,
        /// <see cref="ServerConfig"/> để dùng raw TcpListener.
        /// </summary>
        public static IServer Create(ServerConfig config)
        {
#if NET9_0
            if (config is ServerKestrelConfig kc)
                return ServerKestrel.Create(kc);
            return ServerImplement.Create(config);
#else
            throw new PlatformNotSupportedException("Server requires .NET 9.0 target.");
#endif
        }

        /// <summary>
        /// Danh sách toàn bộ client đang kết nối hiện tại.
        /// </summary>
        IReadOnlyCollection<IConnection> Connections { get; }

        /// <summary>
        /// Tạo JWT token cho client dùng để xác thực WebSocket và REST API.
        /// </summary>
        /// <param name="id">Định danh người dùng.</param>
        /// <param name="name">Tên hiển thị.</param>
        /// <returns>Chuỗi JWT token.</returns>
        string CreateToken(string id, string name);

        /// <summary>
        /// Lấy kết nối theo định danh ID.
        /// </summary>
        /// <param name="id">ID kết nối.</param>
        /// <returns>Đối tượng kết nối tương ứng.</returns>
        IConnection GetConnectionById(string id);

        /// <summary>
        /// Đăng ký callback khi có client mới kết nối thành công.
        /// </summary>
        /// <param name="onConnect">Callback nhận đối tượng kết nối mới.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe OnConnect(Action<IConnection> onConnect);

        /// <summary>
        /// Đăng ký callback khi client ngắt kết nối.
        /// </summary>
        /// <param name="onDisconnect">Callback nhận đối tượng kết nối đã ngắt.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe OnDisconnect(Action<IConnection> onDisconnect);

        /// <summary>
        /// Gửi message qua UDP tới một client cụ thể.
        /// </summary>
        /// <param name="subject">Chủ đề tin nhắn (subject).</param>
        /// <param name="connection">Kết nối nhận tin.</param>
        /// <param name="data">Dữ liệu Protobuf gửi đi.</param>
        void SendOnUdp<TData>(string subject, IConnection connection, TData data) where TData : IMessage<TData>;

        /// <summary>
        /// Gửi message qua TCP (WebSocket) tới một client cụ thể.
        /// </summary>
        /// <param name="subject">Chủ đề tin nhắn (subject).</param>
        /// <param name="connection">Kết nối nhận tin.</param>
        /// <param name="data">Dữ liệu Protobuf gửi đi.</param>
        void SendOnTcp<TData>(string subject, IConnection connection, TData data) where TData : IMessage<TData>;

        /// <summary>
        /// Gửi message qua UDP tới tất cả client đang kết nối.
        /// </summary>
        /// <param name="subject">Chủ đề tin nhắn (subject).</param>
        /// <param name="data">Dữ liệu Protobuf gửi đi.</param>
        void SendAllOnUdp<TData>(string subject, TData data) where TData : IMessage<TData>;

        /// <summary>
        /// Gửi message qua TCP (WebSocket) tới tất cả client đang kết nối.
        /// </summary>
        /// <param name="subject">Chủ đề tin nhắn (subject).</param>
        /// <param name="data">Dữ liệu Protobuf gửi đi.</param>
        void SendAllOnTcp<TData>(string subject, TData data) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng ký nhận message UDP theo chủ đề. Callback nhận kèm tham chiếu tới connection gửi.
        /// </summary>
        /// <param name="subject">Chủ đề đăng ký lắng nghe.</param>
        /// <param name="data">Callback nhận dữ liệu Protobuf kèm connection gửi.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe SubscribeUdp<TData>(string subject, Action<IConnection, TData> data) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng ký nhận message TCP (WebSocket) theo chủ đề. Callback nhận kèm tham chiếu tới connection gửi.
        /// </summary>
        /// <param name="subject">Chủ đề đăng ký lắng nghe.</param>
        /// <param name="data">Callback nhận dữ liệu Protobuf kèm connection gửi.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe SubscribeTcp<TData>(string subject, Action<IConnection, TData> data) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng ký nhận sự kiện cảnh báo từ server (rớt tin, timeout, v.v.).
        /// </summary>
        /// <param name="onWarning">Callback nhận thông tin cảnh báo.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe OnWarning(Action<ServerWarningInfo> onWarning);

        /// <summary>
        /// Đăng ký logic xác thực cho Login REST endpoint (subject "__login__").
        /// </summary>
        /// <param name="authLogic">Hàm nhận dữ liệu login, trả về IUser nếu xác thực thành công; ném exception nếu thất bại.</param>
        void OnLogin<TData>(Func<TData, Task<IUser>> authLogic) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng ký logic xử lý cho GET REST request (không payload).
        /// </summary>
        /// <param name="subject">Chủ đề ánh xạ tới request.</param>
        /// <param name="requestLogic">Hàm xử lý, trả về dữ liệu Protobuf phản hồi.</param>
        void OnGetRequest<TResponse>(string subject, Func<IUser, Task<TResponse>> requestLogic) where TResponse : IMessage<TResponse>;

        /// <summary>
        /// Đăng ký logic xử lý cho POST REST request (có payload).
        /// </summary>
        /// <param name="subject">Chủ đề ánh xạ tới request.</param>
        /// <param name="requestLogic">Hàm xử lý nhận payload và trả về dữ liệu Protobuf phản hồi.</param>
        void OnPostRequest<TRequest, TResponse>(string subject, Func<IUser, TRequest, Task<TResponse>> requestLogic) where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>;
    }
}
