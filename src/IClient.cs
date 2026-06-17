using Google.Protobuf;

namespace MyConnection
{
    /// <summary>
    /// Giao diện phía client. Kết nối WebSocket/UDP tới server, gửi/nhận message, gọi REST API,
    /// và quản lý vòng đời phiên đăng nhập.
    /// </summary>
    public interface IClient : IAsyncDisposable
    {
        /// <summary>
        /// Tạo client từ cấu hình. Hỗ trợ .NET 9.0 và .NET Standard 2.1.
        /// </summary>
        public static IClient Create(ClientConfig config)
        {
            return new ClientImplement(config);
        }


        /// <summary>
        /// Chức năng này cho phép các bên thứ 3 tạo module xử lý chỉ bằng cách kế thừa IClientModule
        /// </summary>
        IClient AddModule(IClientModule clientModule);


        /// <summary>
        /// Mở kết nối WebSocket và bắt tay UDP tới server. Gọi sau <see cref="Login"/>.
        /// </summary>
        Task ConnectServer();

        /// <summary>
        /// Ngắt kết nối WebSocket và UDP nhưng giữ nguyên token. Có thể gọi <see cref="ConnectServer"/> lại để kết nối lại.
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Đăng xuất: xóa token, xóa thông tin tự động đăng nhập lại, và ngắt kết nối. Sau khi gọi, cần <see cref="Login"/> lại.
        /// </summary>
        Task Logout();

        /// <summary>
        /// Gửi message qua UDP tới server.
        /// </summary>
        /// <param name="subject">Chủ đề tin nhắn (subject).</param>
        /// <param name="data">Dữ liệu Protobuf gửi đi.</param>
        void SendOnUdp<TData>(string subject, TData data) where TData : IMessage<TData>;

        /// <summary>
        /// Gửi message qua TCP (WebSocket) tới server.
        /// </summary>
        /// <param name="subject">Chủ đề tin nhắn (subject).</param>
        /// <param name="data">Dữ liệu Protobuf gửi đi.</param>
        void SendOnTcp<TData>(string subject, TData data) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng ký nhận message UDP từ server theo chủ đề.
        /// </summary>
        /// <param name="subject">Chủ đề đăng ký lắng nghe.</param>
        /// <param name="data">Callback nhận dữ liệu Protobuf.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng ký nhận message TCP (WebSocket) từ server theo chủ đề.
        /// </summary>
        /// <param name="subject">Chủ đề đăng ký lắng nghe.</param>
        /// <param name="data">Callback nhận dữ liệu Protobuf.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng ký callback khi WebSocket bị ngắt kết nối (phía server đóng hoặc mạng mất).
        /// </summary>
        /// <param name="onDisconnect">Callback được gọi khi ngắt kết nối.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe OnDisconnect(Action onDisconnect);

        /// <summary>
        /// Đăng ký nhận sự kiện cảnh báo từ client (rớt tin, timeout, lỗi gửi, v.v.).
        /// </summary>
        /// <param name="onWarning">Callback nhận thông tin cảnh báo.</param>
        /// <returns>Handle để hủy đăng ký.</returns>
        ISubscribe OnWarning(Action<WarningInfo> onWarning);

        /// <summary>
        /// Độ trễ khứ hồi UDP mới nhất (RTT), tính bằng millisecond. <c>null</c> nếu UDP không được bật hoặc chưa có kết quả.
        /// </summary>
        long? LatestRttMs { get; }

        /// <summary>
        /// Trạng thái kết nối WebSocket hiện tại. <c>true</c> nếu đang mở.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Đăng nhập vào server qua REST API, trả về thông tin người dùng. Token được lưu nội bộ và tự động gửi kèm các request sau.
        /// Hỗ trợ tự động đăng nhập lại khi token hết hạn.
        /// </summary>
        /// <param name="data">Hàm tạo dữ liệu đăng nhập Protobuf (đồng bộ).</param>
        /// <returns>Thông tin người dùng từ server.</returns>
        Task<IUser> Login<TData>(Func<TData> data) where TData : IMessage<TData>;

        /// <summary>
        /// Đăng nhập vào server qua REST API (bất đồng bộ). Giống <see cref="Login{TData}(Func{TData})"/> nhưng data factory là async.
        /// </summary>
        /// <param name="data">Hàm bất đồng bộ tạo dữ liệu đăng nhập Protobuf.</param>
        /// <returns>Thông tin người dùng từ server.</returns>
        Task<IUser> Login<TData>(Func<Task<TData>> data) where TData : IMessage<TData>;

        /// <summary>
        /// Gửi GET REST request tới server (không payload).
        /// </summary>
        /// <param name="subject">Chủ đề ánh xạ tới handler trên server.</param>
        /// <returns>Dữ liệu Protobuf phản hồi.</returns>
        Task<TResponse> GetRequest<TResponse>(string subject) where TResponse : IMessage<TResponse>;

        /// <summary>
        /// Gửi POST REST request tới server (có payload Protobuf). Hỗ trợ nén payload nếu <see cref="ClientConfig.restCompressedEnable"/> bật.
        /// </summary>
        /// <param name="subject">Chủ đề ánh xạ tới handler trên server.</param>
        /// <param name="body">Dữ liệu Protobuf gửi lên.</param>
        /// <returns>Dữ liệu Protobuf phản hồi.</returns>
        Task<TResponse> PostRequest<TRequest, TResponse>(string subject, TRequest body) where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>;
    }
}
