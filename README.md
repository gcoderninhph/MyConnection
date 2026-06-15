# MyConnection

Thư viện kết nối mạng client-server cho .NET, hỗ trợ WebSocket + UDP + REST, xác thực JWT, pub/sub messaging theo subject. Thiết kế để hoạt động trên cả server (.NET 9.0) và Unity client (netstandard2.1, IL2CPP-safe).

---

## Kiến trúc tổng quan

```
┌─────────────────────────────────────────────────────────────────┐
│                          CLIENT                                  │
│                                                                  │
│  IClient ──→ ClientAbstract ──→ ClientImplement                  │
│   │                              │                               │
│   │  Login(data)                 │  WebSocket (Native)           │
│   │  ConnectServer()             │  UdpClientWrapper             │
│   │  DisconnectAsync()           │  SubjectDispatcher×2          │
│   │  Logout()                    │  UdpPingService (RTT)         │
│   │  SendOnTcp / SendOnUdp       │  HttpClient (REST)            │
│   │  SubscribeTcp / SubscribeUdp │  Auto re-login                │
│   │  GetRequest / PostRequest    │  Warning (W001-W007)          │
│   │  OnDisconnect / OnWarning    │                               │
│   │  IsConnected / LatestRttMs   │                               │
│                                                                  │
└──────────────────────────┬──────────────────────────────────────┘
                           │
          HTTP/REST  ←─────┤─────→  WebSocket  ←─────→  UDP
          (Protobuf)       │        (Protobuf)          (Protobuf)
          POST /api        │        ws://host:port/ws   127.0.0.1:9091
                           │
┌──────────────────────────┴──────────────────────────────────────┐
│                          SERVER                                  │
│                                                                  │
│  IServer ──→ ServerAbstract ──→ ServerImplement                 │
│   │                              │                               │
│   │  Create(config)              │  WebSocketListener (TCP)      │
│   │  Connections                 │  UdpListener                  │
│   │  CreateToken / GetById       │  ConnectionRegistry           │
│   │  OnConnect / OnDisconnect    │  ServerTokenService (JWT)     │
│   │  SendOnXxx / SendAllOnXxx    │  UdpSessionMap                │
│   │  SubscribeXxx                │  UdpHandshakeHandler           │
│   │  OnLogin / OnGet / OnPost    │  REST dispatch                │
│   │  OnWarning                   │  Warning (W001-W007)          │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

---

## Tính năng

| Tính năng | Mô tả |
|---|---|
| **WebSocket** | TCP real-time 2 chiều, JWT auth qua header `Authorization: Bearer` |
| **UDP** | Bắt tay auth, ping/pong đo RTT, gửi message nhanh 1 chiều |
| **REST API** | Login + GetRequest + PostRequest qua HTTP POST, Protobuf encode |
| **Pub/Sub** | Đăng ký nhận message theo `subject` (string), hỗ trợ TCP + UDP riêng |
| **JWT** | Token-based auth, `CreateToken` server-side, auto gửi kèm mọi request |
| **RTT** | Đo round-trip time UDP, property `LatestRttMs` trên client |
| **Warning** | 7 mã cảnh báo W001-W007, callback `OnWarning` cho client và server |
| **Auto re-login** | Client tự động đăng nhập lại khi token hết hạn (1 lần retry) |
| **Compression** | Deflate payload POST request (tùy chọn `restCompressedEnable`) |
| **Reconnect-safe** | `DisconnectAsync` giữ token, `Logout` mới xóa token |
| **Dispose pattern** | `IAsyncDisposable`, `await using` tự dọn dẹp |
| **IL2CPP/Unity** | `netstandard2.1` target, `MessageParser<T>` AOT-safe, DLL bundle thẳng |

---

## Gói NuGet & phụ thuộc

| Target | NuGet dependencies |
|---|---|
| `net9.0` | `Google.Protobuf 3.34.1`, `Microsoft.IdentityModel.Tokens 8.*`, `System.IdentityModel.Tokens.Jwt 8.*`, `Colyseus.NativeWebSocket 2.*` |
| `netstandard2.1` (Unity) | `Google.Protobuf.dll` + `NativeWebSocket.dll` (bundle từ `libs/`) |

`Grpc.Tools 2.80.0` dùng để compile `.proto` tại build-time (PrivateAssets=all).

---

## Cấu trúc thư mục

```
MyConnection/
├── src/                         # Mã nguồn thư viện
│   ├── IClient.cs               # Interface client
│   ├── ClientAbstract.cs        # Abstract class client (logic ConnectServer chung)
│   ├── IServer.cs               # Interface server
│   ├── ServerAbstract.cs        # Abstract class server
│   ├── IConnection.cs           # Interface kết nối (id, user, attributes)
│   ├── IUser.cs                 # Interface người dùng (id, name)
│   ├── ISubscribe.cs            # Handle hủy đăng ký subscribe
│   ├── ConnectionFailedException.cs
│   └── impl/                    # Triển khai
│       ├── ClientConfig.cs      # Cấu hình client
│       ├── ClientImplement.cs   # Client implementation đầy đủ
│       ├── ServerConfig.cs      # Cấu hình server
│       ├── ServerImplement.cs   # Server implementation đầy đủ
│       ├── ConnectionImplement.cs
│       ├── ConnectionRegistry.cs
│       ├── SubjectDispatcher.cs # Dispatcher pub/sub
│       ├── ProtoSerializer.cs   # Serialize/Deserialize Protobuf
│       ├── WebSocketListener.cs # HTTP+WebSocket server tự build
│       ├── UdpListener.cs       # UDP server
│       ├── UdpClientWrapper.cs  # UDP client
│       ├── UdpSessionMap.cs     # Ánh xạ connection → UDP endpoint
│       ├── UdpHandshakeHandler.cs
│       ├── UdpPingService.cs    # Ping/pong RTT
│       ├── ServerTokenService.cs # Sinh + verify JWT
│       ├── ApiException.cs      # Exception cho REST API
│       └── WarningInfo.cs       # Thông tin cảnh báo
├── protos/                      # Protocol Buffer schema
│   ├── api_request.proto
│   ├── api_response.proto
│   ├── login_response.proto
│   ├── message_envelope.proto
│   └── string_value.proto
├── MyConnection.Tests/          # xUnit tests (net9.0)
│   └── ConnectionTests.cs       # 13 tests: A1-A3, B1-B3, C1-C3, D1-D4
├── ConsoleDemo/                 # Demo server console app
│   ├── ConsoleDemo.csproj
│   └── Program.cs
├── plan/                        # Tài liệu thiết kế
│   ├── api-call-system.md
│   └── logout.md
├── libs/                        # DLL bundle cho netstandard2.1
│   ├── Google.Protobuf.dll
│   └── NativeWebSocket.dll
├── nupkgs/                      # NuGet package output
│   └── MyConnection.1.0.0.nupkg
└── MyConnection.csproj
```

---

## Interface chính

### IClient

```csharp
public interface IClient : IAsyncDisposable
{
    static IClient Create(ClientConfig config);

    // Connection lifecycle
    Task ConnectServer();           // Mở WS + bắt tay UDP, gọi sau Login
    Task DisconnectAsync();         // Ngắt WS + UDP, giữ token để reconnect
    Task Logout();                  // Xóa token + disconnect, cần Login lại

    // Messaging (Protobuf)
    void SendOnUdp<TData>(string subject, TData data);
    void SendOnTcp<TData>(string subject, TData data);

    // Subscribe
    ISubscribe SubscribeUdp<TData>(string subject, Action<TData> callback);
    ISubscribe SubscribeTcp<TData>(string subject, Action<TData> callback);

    // Events
    ISubscribe OnDisconnect(Action callback);
    ISubscribe OnWarning(Action<WarningInfo> callback);

    // Status
    bool IsConnected { get; }
    long? LatestRttMs { get; }

    // REST API
    Task<IUser> Login<TData>(Func<TData> data);
    Task<IUser> Login<TData>(Func<Task<TData>> data);
    Task<TResponse> GetRequest<TResponse>(string subject);
    Task<TResponse> PostRequest<TRequest, TResponse>(string subject, TRequest body);
}
```

### IServer

```csharp
public interface IServer : IAsyncDisposable
{
    static IServer Create(ServerConfig config);  // Chỉ .NET 9.0

    IReadOnlyCollection<IConnection> Connections { get; }
    string CreateToken(string id, string name);
    IConnection GetConnectionById(string id);

    // Events
    ISubscribe OnConnect(Action<IConnection> callback);
    ISubscribe OnDisconnect(Action<IConnection> callback);
    ISubscribe OnWarning(Action<ServerWarningInfo> callback);

    // Targeted send
    void SendOnTcp<TData>(string subject, IConnection conn, TData data);
    void SendOnUdp<TData>(string subject, IConnection conn, TData data);

    // Broadcast
    void SendAllOnTcp<TData>(string subject, TData data);
    void SendAllOnUdp<TData>(string subject, TData data);

    // Subscribe (receive from clients)
    ISubscribe SubscribeTcp<TData>(string subject, Action<IConnection, TData> callback);
    ISubscribe SubscribeUdp<TData>(string subject, Action<IConnection, TData> callback);

    // REST handlers
    void OnLogin<TData>(Func<TData, Task<IUser>> authLogic);
    void OnGetRequest<TResponse>(string subject, Func<Task<TResponse>> handler);
    void OnPostRequest<TRequest, TResponse>(string subject, Func<TRequest, Task<TResponse>> handler);
}
```

### IConnection

```csharp
public interface IConnection
{
    string Id { get; }                       // Id kết nối duy nhất
    IUser User { get; }                      // Thông tin người dùng
    IDictionary<string, object> Attributes { get; }  // Dữ liệu tùy chỉnh
    bool Connected { get; }                  // Trạng thái kết nối
    string UdpAddress { get; }              // Endpoint UDP của client
    string WebSocketSessionId { get; }
    long UdpPingTime { get; }
    long WebSocketPingTime { get; }
}
```

---

## Cấu hình

### ClientConfig

| Field | Mặc định | Mô tả |
|---|---|---|
| `tcpServer` | `"127.0.0.1:9090"` | Địa chỉ WebSocket server (host:port) |
| `websocketEnpoint` | `"/ws"` | Path WebSocket endpoint |
| `restEndpoint` | `"/api"` | Path REST API endpoint |
| `restCompressedEnable` | `false` | Bật nén Deflate payload POST |
| `tcpSecurity` | `false` | `true` = wss:// + https:// |
| `udpServer` | `"127.0.0.1:9091"` | Địa chỉ UDP server. Để `""` nếu không dùng |
| `udpPingIntervalMs` | `5000` | Chu kỳ ping UDP (millisecond) |
| `udpPingTimeoutMs` | `15000` | Timeout ping UDP (millisecond) |

### ServerConfig

| Field | Mặc định | Mô tả |
|---|---|---|
| `tcpPort` | `9090` | Cổng TCP cho HTTP/WebSocket |
| `websocketEndpoint` | `"/ws"` | Path WebSocket endpoint |
| `restEndpoint` | `"/api"` | Path REST API endpoint |
| `restCompressedEnable` | `false` | Hỗ trợ giải nén payload |
| `udpPort` | `9091` | Cổng UDP |
| `jwtSecret` | `""` | Khóa bí mật JWT (bắt buộc) |
| `jwtAudience` | `""` | Audience claim trong JWT |
| `jwtIssuer` | `""` | Issuer claim trong JWT |

---

## Vòng đời kết nối (Client Lifecycle)

```
Login(data)  ──→  REST POST /api subject=__login__  →  server xác thực → trả LoginResponse
    │
    ├── _token = loginResult.Token
    ├── _reLoginFactory = closure auto re-login
    └── _loginDataFactory = data factory

ConnectServer()  ──→  WebSocket ws://host:9090/ws + header Bearer <token>
    │                   → server ValidateToken → WebSocket upgrade
    │
    ├── Bắt tay UDP: request_udp_auth → server cấp key → ping 5 lần → bound
    └── UdpPingService.Start() đo RTT mỗi 5s

━━━━━━━  Đã kết nối  ━━━━━━━
SendOnTcp / SendOnUdp / GetRequest / PostRequest
SubscribeTcp / SubscribeUdp
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

DisconnectAsync()  ──→  đóng WS + UDP, _token giữ nguyên
    │
ConnectServer()  ──→  reconnect OK (token còn valid)

Logout()  ──→  xóa _token + _reLoginFactory + disconnect

DisposeAsync()  ──→  Logout() + cleanup _udpClient + _sendLock
```

---

## Giao thức message

Mọi message trao đổi qua WebSocket và UDP đều dùng `MessageEnvelope`:

```protobuf
message MessageEnvelope {
  string subject = 1;       // Chủ đề (VD: "chat", "move", "__ping__", "__pong__")
  bytes payload = 2;        // Dữ liệu Protobuf (IMessage<T>.ToByteArray())
  bytes session_key = 3;    // Key session UDP
  int64 ticks = 4;          // Timestamp (dùng cho RTT)
}
```

- **`subject`**: string phân biệt chủ đề, dùng để route đến subscriber
- **`payload`**: byte[] serialize từ bất kỳ `IMessage<T>` nào
- **Các subject đặc biệt**: `__login__`, `__ping__`, `__pong__`, `__udp_auth__`, `__udp_bound__`, `request_udp_auth`

---

## REST API system

Client gọi `Login`, `GetRequest`, `PostRequest` → HTTP POST `/{restEndpoint}` với body `ApiRequest` Protobuf.

```protobuf
message ApiRequest {
  string subject = 1;       // Chủ đề (__login__ hoặc subject đăng ký)
  bytes payload = 2;        // Dữ liệu gửi lên (đã nén nếu Compressed=true)
  bool compressed = 3;      // Đánh dấu payload đã nén Deflate
  bool has_payload = 4;     // true = POST-style, false = GET-style
  string token = 5;         // JWT token (trừ __login__)
}

message ApiResponse {
  string subject = 1;
  bytes payload = 2;
  bool compressed = 3;
  bool success = 4;
  string error_code = 5;    // "" nếu thành công, "TokenExpired", ...
  string error_message = 6;
}
```

**Luồng xử lý REST trên server**:
1. Nhận HTTP POST → parse `ApiRequest`
2. Nếu `subject != "__login__"` → validate JWT token
3. Nếu `Compressed` → giải nén payload
4. Route theo subject → `_getHandlers` hoặc `_postHandlers`
5. Serialize kết quả → `ApiResponse`
6. Trả về HTTP response

**Auto re-login trên client**: khi `GetRequest`/`PostRequest` gặp `ApiResponse.error_code == "TokenExpired"`, gọi `_reLoginFactory` 1 lần để lấy token mới → retry request.

---

## Hệ thống cảnh báo

| Mã | Mô tả | Phía |
|---|---|---|
| W001 | UDP ping timeout, kết nối có thể đã mất | Client / Server |
| W002 | UDP handshake thất bại sau 5 lần thử | Client / Server |
| W003 | Gửi TCP thất bại, WebSocket chưa kết nối | Client |
| W004 | Gửi UDP thất bại | Client |
| W005 | WebSocket đã đóng | Client |
| W006 | Tin nhắn TCP bị rơi, không có subscriber | Client / Server |
| W007 | Tin nhắn UDP bị rơi, không có subscriber | Client / Server |

`WarningInfo` (client) chứa `Code`, `Message`, `Exception`.  
`ServerWarningInfo : WarningInfo` thêm `Connection` để biết cảnh báo liên quan đến client nào.

---

## Xử lý lỗi

| Exception | Khi nào | Cách xử lý |
|---|---|---|
| `ConnectionFailedException` | Không kết nối được WebSocket, token sai/hết hạn, timeout 10s | Bắt khi `ConnectServer()`, kiểm tra token |
| `ApiException` | REST API trả về `success=false` | `.ErrorCode` + `.ErrorMessage` |
| `InvalidOperationException` | Gọi `ConnectServer()` trước `Login()` hoặc trước `IClient.Create()` | Kiểm tra thứ tự gọi |
| `PlatformNotSupportedException` | Gọi `IServer.Create()` trên `netstandard2.1` | Server chỉ hỗ trợ .NET 9.0 |
| `KeyNotFoundException` | `GetConnectionById()` không tìm thấy | Kiểm tra ID kết nối |
| `ObjectDisposedException` | Thao tác sau khi dispose | Bị swallow trong receive loop, không propagate |

---

## Hỗ trợ nền tảng

| Nền tảng | Client | Server |
|---|---|---|
| Windows (.NET 9.0) | Có | Có |
| Linux (.NET 9.0) | Có | Có |
| macOS (.NET 9.0) | Có | Có |
| Unity Standalone (Win/Mac/Linux) | Có (`netstandard2.1`) | Không |
| Unity iOS | Có | Không |
| Unity Android | Có | Không |
| Unity WebGL | **Không** (HttpClient không hỗ trợ trong browser sandbox) | Không |

---

## Ví dụ sử dụng

### Server (.NET 9.0)

```csharp
using MyConnection;

var config = new ServerConfig
{
    tcpPort = 9090,
    websocketEndpoint = "/ws",
    restEndpoint = "/api",
    udpPort = 9091,
    jwtSecret = "your-secret-key-at-least-32-bytes!!",
    jwtIssuer = "my-issuer",
    jwtAudience = "my-audience"
};

var server = (ServerImplement)ServerImplement.Create(config);

// Đăng ký xác thực login
server.OnLogin<StringValue>(async data =>
{
    if (data.Value == "admin")
        return new MyUser("1", "Admin");
    throw new Exception("Sai thông tin đăng nhập");
});

// Đăng ký REST handlers
server.OnGetRequest<StringValue>("ping", () => Task.FromResult(new StringValue { Value = "pong" }));
server.OnPostRequest<StringValue, StringValue>("echo", req => Task.FromResult(req));

// Đăng ký sự kiện kết nối
server.OnConnect(conn =>
{
    Console.WriteLine($"[+] {conn.User.Name} đã kết nối (Id={conn.Id})");
});

server.OnDisconnect(conn =>
{
    Console.WriteLine($"[-] {conn.User.Name} đã ngắt kết nối");
});

// Đăng ký nhận message từ client qua TCP
server.SubscribeTcp<StringValue>("chat", (conn, msg) =>
{
    Console.WriteLine($"{conn.User.Name}: {msg.Value}");
    // Broadcast tới tất cả client
    server.SendAllOnTcp("chat", msg);
});

// Tạo token cho client
var token = server.CreateToken("1", "Admin");
Console.WriteLine($"Token: {token}");

await Task.Delay(-1); // Giữ server chạy
```

### Client

```csharp
using MyConnection;

var config = new ClientConfig
{
    tcpServer = "127.0.0.1:9090",
    websocketEnpoint = "/ws",
    restEndpoint = "/api",
    udpServer = ""
};

var client = IClient.Create(config);

// Đăng ký sự kiện
client.OnDisconnect(() => Console.WriteLine("[!] Mất kết nối"));
client.OnWarning(w => Console.WriteLine($"[W] {w.Code}: {w.Message}"));

// Đăng ký nhận message
client.SubscribeTcp<StringValue>("chat", msg =>
{
    Console.WriteLine($"Chat: {msg.Value}");
});

// Đăng nhập
var user = await client.Login(() => new StringValue { Value = "admin" });
Console.WriteLine($"Logged in as {user.Name} (Id={user.Id})");

// Kết nối WebSocket
await client.ConnectServer();

// Gửi message
client.SendOnTcp("chat", new StringValue { Value = "Xin chào!" });

// REST API
var pong = await client.GetRequest<StringValue>("ping");
Console.WriteLine(pong.Value); // "pong"

var echo = await client.PostRequest<StringValue, StringValue>("echo", new StringValue { Value = "hello" });
Console.WriteLine(echo.Value); // "hello"

// Ngắt kết nối (giữ token)
await client.DisconnectAsync();

// Kết nối lại
await client.ConnectServer();

// Đăng xuất (xóa token)
await client.Logout();

// Dọn dẹp
await client.DisposeAsync();
```

---

## Testing

```bash
dotnet test
```

13 test cases trong `ConnectionTests.cs`:

| Nhóm | Test | Mô tả |
|---|---|---|
| **A** Connect | A1 | Kết nối với token hợp lệ |
| | A2 | Token sai → throw exception |
| | A3 | Token hết hạn → throw exception |
| **B** Message | B1 | Client gửi → server nhận |
| | B2 | Server gửi → client nhận |
| | B3 | 2 client chat qua server (subscribe connection) |
| **C** Disconnect | C1 | Server ngắt → client `OnDisconnect` |
| | C2 | Client ngắt → server `OnDisconnect` |
| | C3 | Reconnect, subscription vẫn hoạt động |
| **D** Warning | D1 | W003 khi gửi TCP trước khi kết nối |
| | D2 | W006 khi nhận subject không subscriber |
| | D3 | Server W006 khi route đến subject không subscriber |
| | D4 | `LatestRttMs` null khi UDP tắt |

Test dùng `TestClient` extend `ClientAbstract` với `ClientWebSocket` của .NET, chạy cùng `ServerImplement` thật.

---

## Build & Pack

```bash
# Build cả 2 target
dotnet build

# Chạy test
dotnet test

# Build NuGet package
dotnet pack -c Release -o nupkgs

# Publish ConsoleDemo (self-contained win-x64)
dotnet publish ConsoleDemo -c Release --self-contained true -r win-x64 -o ConsoleDemo\publish
```

---

## License

MIT
