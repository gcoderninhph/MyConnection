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
│  IServer ──→ ServerAbstract → ServerCore (shared logic)         │
│   │              │               ├── ServerImplement (raw TCP)   │
│   │              │               └── ServerKestrel  (Kestrel)    │
│   │              │                                               │
│   │              │         ┌─────────────────────────┐           │
│   │              │         │ Shared:                  │           │
│   │              │         │ • ConnectionRegistry     │           │
│   │              │         │ • ServerTokenService     │           │
│   │              │         │ • UdpListener            │           │
│   │              │         │ • UdpSessionMap          │           │
│   │              │         │ • UdpHandshakeHandler    │           │
│   │              │         │ • REST dispatch          │           │
│   │              │         └─────────────────────────┘           │
│   │                                                              │
│   │  Create(config)        Tự chọn impl: ServerKestrelConfig     │
│   │  Connections           → ServerKestrel, ServerConfig         │
│   │  CreateToken / GetById → ServerImplement                     │
│   │  OnConnect/OnDisconnect                                      │
│   │  SendOnXxx / SendAllOnXxx                                    │
│   │  SubscribeXxx                                                │
│   │  OnLogin / OnGet / OnPost                                    │
│   │  OnWarning                                                   │
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
| **Dual server** | 2 transport: raw `TcpListener` (`ServerImplement`) và ASP.NET Core Kestrel (`ServerKestrel`) |
| **DI integration** | `AddMyConnectionServer()` + `UseMyConnectionServer()`, shared `WebApplication` pipeline |
| **Module system** | `AddModule(IClientModule)` / `AddModule(IServerModule)` — plugin bên thứ 3 không cần kế thừa factory |

---

## Gói NuGet & phụ thuộc

| Target | NuGet dependencies |
|---|---|
| `net9.0` | `Google.Protobuf 3.34.1`, `Microsoft.IdentityModel.Tokens 8.*`, `System.IdentityModel.Tokens.Jwt 8.*`, `Colyseus.NativeWebSocket 2.*` |
| `netstandard2.1` (Unity) | `Google.Protobuf.dll` + `NativeWebSocket.dll` (bundle từ `libs/`) |

`Grpc.Tools 2.80.0` dùng để compile `.proto` tại build-time (PrivateAssets=all).  
`FrameworkReference Microsoft.AspNetCore.App` cho server Kestrel (net9.0 only).

---

## Cấu trúc thư mục

```
MyConnection/
├── src/                              # Mã nguồn thư viện
│   ├── IClient.cs                    # Interface client
│   ├── ClientAbstract.cs             # Abstract class client
│   ├── IServer.cs                    # Interface server + static factory
│   ├── ServerAbstract.cs             # Abstract class server
│   ├── ServerCore.cs                 # Shared logic (net9.0)
│   ├── IConnection.cs                # Interface kết nối
│   ├── IUser.cs                      # Interface người dùng
│   ├── ISubscribe.cs                 # Handle hủy đăng ký subscribe
│   ├── IServerModule.cs               # Interface module phía server
│   ├── IClientModule.cs               # Interface module phía client
│   ├── ConnectionFailedException.cs
│   └── impl/                         # Triển khai
│       ├── ClientConfig.cs           # Cấu hình client
│       ├── ClientImplement.cs        # Client implementation
│       ├── ServerConfig.cs           # Cấu hình server (raw TCP)
│       ├── ServerKestrelConfig.cs     # Cấu hình server Kestrel
│       ├── ServerImplement.cs        # Server raw TcpListener
│       ├── ServerKestrel.cs          # Server ASP.NET Core Kestrel
│       ├── ConnectionImplement.cs    # Connection wrapper
│       ├── ConnectionRegistry.cs     # Tracking + routing
│       ├── SubjectDispatcher.cs      # Pub/sub dispatcher (2x)
│       ├── ProtoSerializer.cs        # Protobuf serialize/deserialize
│       ├── WebSocketListener.cs      # HTTP+WebSocket raw TCP
│       ├── UdpListener.cs            # UDP server
│       ├── UdpClientWrapper.cs       # UDP client
│       ├── UdpSessionMap.cs          # Connection → UDP endpoint
│       ├── UdpHandshakeHandler.cs    # Sinh key UDP
│       ├── UdpPingService.cs         # Ping/pong RTT
│       ├── KestrelHostedService.cs   # DI extension AddMyConnectionServer
│       ├── MyConnectionWebApplicationExtensions.cs  # UseMyConnectionServer
│       ├── ServerTokenService.cs     # JWT create + validate
│       ├── ApiException.cs           # REST API exception
│       └── WarningInfo.cs            # Thông tin cảnh báo
├── protos/                           # Protocol Buffer schema
│   ├── api_request.proto
│   ├── api_response.proto
│   ├── login_response.proto
│   ├── message_envelope.proto
│   └── string_value.proto
├── MyConnection.Tests/               # xUnit tests (net9.0)
│   └── ConnectionTests.cs            # 13 tests
├── ConsoleDemo/                      # Demo server console app
│   ├── ConsoleDemo.csproj
│   └── Program.cs                    # ASP.NET Core + Kestrel demo
├── plan/                             # Tài liệu thiết kế
│   ├── kestrel-server.md             # Plan ServerKestrel (đã hoàn thành)
│   ├── kestrel-integration.md        # Refactor tích hợp WebApplication
│   ├── api-call-system.md
│   └── logout.md
├── libs/                             # DLL bundle cho netstandard2.1
│   ├── Google.Protobuf.dll
│   └── NativeWebSocket.dll
├── nupkgs/                           # NuGet package output
│   └── MyConnection.1.0.2.nupkg
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
    void OnGetRequest<TResponse>(string subject, Func<IUser, Task<TResponse>> handler);
    void OnPostRequest<TRequest, TResponse>(string subject, Func<IUser, TRequest, Task<TResponse>> handler);
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

### ServerConfig (raw TCP — `ServerImplement`)

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

### ServerKestrelConfig (Kestrel — `ServerKestrel`, kế thừa `ServerConfig`)

| Field | Mặc định | Mô tả |
|---|---|---|
| `KestrelUrls` | `"http://0.0.0.0:9090"` | URL cho Kestrel lắng nghe. Ghi đè port so với `tcpPort` |
| *Kế thừa từ ServerConfig* | | `websocketEndpoint`, `restEndpoint`, `restCompressedEnable`, `udpPort`, `jwtSecret`, `jwtAudience`, `jwtIssuer` |

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
2. Nếu `subject != "__login__"` → validate JWT token → extract `IUser` từ claims (`sub` → Id, `name` → Name)
3. Nếu `Compressed` → giải nén payload
4. Route theo subject → `_getHandlers` hoặc `_postHandlers` (truyền `IUser` vào handler)
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

## Hệ thống Module (AddModule)

Module là cách mở rộng server/client mà không cần kế thừa factory. Bên thứ 3 chỉ cần implement `IServerModule` hoặc `IClientModule`.

| Interface | Method | Mô tả |
|---|---|---|
| `IClientModule` | `SetIClient(IClient client)` | Nhận tham chiếu `IClient`, đăng ký handler, subscribe, hook sự kiện |
| `IServerModule` | `SetServer(IServer server)` | Nhận tham chiếu `IServer`, đăng ký REST handler, subscribe, hook sự kiện |

### Ví dụ — Client module

```csharp
public class ChatClientModule : IClientModule
{
    public void SetIClient(IClient client)
    {
        client.SubscribeTcp<ChatMessage>("chat", msg =>
            Debug.Log($"[{msg.Sender}]: {msg.Text}")
        );
    }
}

var client = IClient.Create(config);
client.AddModule(new ChatClientModule());
await client.Login(() => new LoginRequest { ... });
await client.ConnectServer();
```

### Ví dụ — Server module

```csharp
public class AuthServerModule : IServerModule
{
    public void SetServer(IServer server)
    {
        server.OnLogin<LoginRequest>(async data =>
        {
            if (data.Username == "admin") return new UserInfo("1", "Admin");
            throw new Exception("Sai thông tin");
        });

        server.OnGetRequest<StringValue>("server_time", user =>
        {
            Debug.Log($"[{user.Name}] requested time");
            return Task.FromResult(new StringValue { Value = DateTime.Now.ToString() });
        });
    }
}

var server = (ServerImplement)IServer.Create(config);
server.AddModule(new AuthServerModule());
server.AddModule(new ChatServerModule());
```

> **Lưu ý**: `AddModule` trả về `this` để chain fluent: `server.AddModule(a).AddModule(b)`.

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

### Server — Raw TCP (`ServerImplement`)

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

await using var server = (ServerImplement)IServer.Create(config);

server.OnLogin<StringValue>(async data =>
{
    if (data.Value == "admin")
        return new MyUser("1", "Admin");
    throw new Exception("Sai thông tin đăng nhập");
});

server.OnConnect(conn =>
    Console.WriteLine($"[+] {conn.User.Name} đã kết nối (Id={conn.Id})"));

server.SubscribeTcp<StringValue>("chat", (conn, msg) =>
{
    Console.WriteLine($"{conn.User.Name}: {msg.Value}");
    server.SendAllOnTcp("chat", msg);
});

Console.WriteLine($"Token: {server.CreateToken("1", "Admin")}");
await Task.Delay(-1); // Giữ server chạy
```

### Server — Kestrel standalone (`ServerKestrel`)

```csharp
using MyConnection;

var config = new ServerKestrelConfig
{
    KestrelUrls = "http://0.0.0.0:9090",
    websocketEndpoint = "/ws",
    restEndpoint = "/api",
    udpPort = 9091,
    jwtSecret = "your-secret-key-at-least-32-bytes!!",
    jwtIssuer = "my-issuer",
    jwtAudience = "my-audience"
};

await using var server = (ServerKestrel)IServer.Create(config);

// Đăng ký handlers giống hệt ServerImplement
server.OnLogin<StringValue>(...);
server.SubscribeTcp<StringValue>("chat", ...);

await Task.Delay(-1); // Server tự quản lý Kestrel lifecycle
```

### Server — ASP.NET Core DI (`ServerKestrel` + shared pipeline)

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MyConnection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMyConnectionServer(new ServerKestrelConfig
{
    KestrelUrls = "http://0.0.0.0:9090",
    websocketEndpoint = "/ws",
    restEndpoint = "/api",
    udpPort = 9091,
    jwtSecret = "your-secret-key-at-least-32-bytes!!",
    jwtIssuer = "my-issuer",
    jwtAudience = "my-audience"
});

var app = builder.Build();

var server = app.Services.GetRequiredService<IServer>();

// Đăng ký handlers
server.OnLogin<StringValue>(...);
server.OnConnect(conn => Console.WriteLine($"[+] {conn.User.Name} connected"));
server.SubscribeTcp<StringValue>("chat", (conn, msg) =>
{
    server.SendAllOnTcp("chat", msg);
});

// Đăng ký MyConnection middleware vào shared WebApplication pipeline
app.UseMyConnectionServer();

await app.RunAsync(); // 1 Kestrel duy nhất
```

> **Lưu ý**: `UseMyConnectionServer()` tự động áp `KestrelUrls` từ config vào host Kestrel và xóa default `localhost:5000` — chỉ 1 cổng duy nhất. Server lifecycle do `KestrelHostedService` (IHostedService) quản lý.

### So sánh 3 cách dùng server

| | ServerImplement | ServerKestrel (standalone) | ServerKestrel (DI) |
|---|---|---|---|
| Transport | Raw TcpListener | Kestrel nội bộ | Kestrel shared host |
| Kestrel instances | 0 | 1 | 1 (shared) |
| DI container | Không | Không | Có (`AddMyConnectionServer`) |
| Middleware | Không | Không | Có (`UseMyConnectionServer`) |
| Startup | `IServer.Create(ServerConfig)` | `IServer.Create(ServerKestrelConfig)` | `WebApplication.CreateBuilder` |
| Lifecycle | `await using` | `await using` | `IHostedService` |

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

client.OnDisconnect(() => Console.WriteLine("[!] Mất kết nối"));
client.OnWarning(w => Console.WriteLine($"[W] {w.Code}: {w.Message}"));

client.SubscribeTcp<StringValue>("chat", msg =>
    Console.WriteLine($"Chat: {msg.Value}"));

var user = await client.Login(() => new StringValue { Value = "admin" });
Console.WriteLine($"Logged in as {user.Name} (Id={user.Id})");

await client.ConnectServer();

client.SendOnTcp("chat", new StringValue { Value = "Xin chào!" });

var pong = await client.GetRequest<StringValue>("ping");
Console.WriteLine(pong.Value); // "pong"

var echo = await client.PostRequest<StringValue, StringValue>("echo",
    new StringValue { Value = "hello" });
Console.WriteLine(echo.Value); // "hello"

await client.DisconnectAsync();  // Giữ token
await client.ConnectServer();    // Reconnect
await client.Logout();           // Xóa token
await client.DisposeAsync();     // Dọn dẹp
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
| | B3 | 2 client chat qua server |
| **C** Disconnect | C1 | Server ngắt → client `OnDisconnect` |
| | C2 | Client ngắt → server `OnDisconnect` |
| | C3 | Reconnect, subscription vẫn hoạt động |
| **D** Warning | D1 | W003 khi gửi TCP trước khi kết nối |
| | D2 | W006 khi nhận subject không subscriber |
| | D3 | Server W006 khi route đến subject không subscriber |
| | D4 | `LatestRttMs` null khi UDP tắt |

---

## Build & Pack

```bash
# Build cả 2 target (net9.0 + netstandard2.1)
dotnet build                              # 0 error, 0 warning

# Chạy test
dotnet test                               # 13/13 passed

# Build NuGet package
dotnet pack -c Release -o nupkgs          # → nupkgs/MyConnection.1.0.2.nupkg

# Build + chạy ConsoleDemo
dotnet build ConsoleDemo/ConsoleDemo.csproj
dotnet run --project ConsoleDemo

# Publish ConsoleDemo self-contained (không cần cài .NET)
dotnet publish ConsoleDemo -c Release --self-contained true -r win-x64 -o ConsoleDemo\publish
```

---

## License

MIT
