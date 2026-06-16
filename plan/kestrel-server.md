# Plan: ServerKestrel — MyConnection chạy trên Kestrel

## Mục tiêu

- `ServerKestrel : ServerCore : ServerAbstract` — cùng `IServer` interface, cùng `IServer.Create(config)`, bên trong chạy Kestrel thay raw `TcpListener`
- Giữ nguyên `ServerImplement` (raw socket) cho standalone
- HTTP/WS trước, TLS để sau
- `AddMyConnectionServer()` extension method để tích hợp DI chuẩn ASP.NET Core

---

## Tổng quan thay đổi

| # | File | Thao tác | Mô tả |
|---|------|----------|-------|
| 1 | `src/ServerCore.cs` | **MỚI** | Base class chứa toàn bộ shared logic giữa 2 impl |
| 2 | `src/impl/ServerKestrel.cs` | **MỚI** | Kestrel transport (`#if NET9_0`) |
| 3 | `src/impl/ServerKestrelConfig.cs` | **MỚI** | Config mở rộng: `KestrelUrls` |
| 4 | `src/impl/KestrelHostedService.cs` | **MỚI** | `IHostedService` wrapper + DI extension |
| 5 | `src/impl/ServerImplement.cs` | **SỬA** | Kế thừa `ServerCore`, chỉ giữ `WebSocketListener` |
| 6 | `src/impl/WebSocketListener.cs` | **SỬA** | `ReceiveLoop` → gọi `ServerCore.ReceiveLoop` static |
| 7 | `src/IServer.cs` | **SỬA** | `Create()` tự chọn impl theo loại config |
| 8 | `src/MyConnection.csproj` | **SỬA** | Thêm `FrameworkReference Include="Microsoft.AspNetCore.App"` |
| 9 | `MyConnection.Tests/ConnectionTests.cs` | **SỬA** | Thêm test cho `ServerKestrel` |
| 10 | `GUIDE.md` | **SỬA** | Thêm mục hướng dẫn `ServerKestrel` |
| 11 | `plan/kestrel-server.md` | **MỚI** | File plan này |

---

## Bước 1: `ServerCore` — tách shared logic

`src/ServerCore.cs`, `#if NET9_0`, kế thừa `ServerAbstract`

### Fields (protected, chuyển từ `ServerImplement`)

| Field | Kiểu | Vai trò |
|-------|------|--------|
| `_config` | `ServerConfig` | Cấu hình |
| `_tokenService` | `ServerTokenService` | JWT create/validate |
| `_registry` | `ConnectionRegistry` | Connection tracking + routing |
| `_sessionMap` | `UdpSessionMap` | UDP endpoint mapping |
| `_handshakeHandler` | `UdpHandshakeHandler` | Sinh key UDP |
| `_udpListener` | `UdpListener?` | UDP socket |
| `_cts` | `CancellationTokenSource` | Global shutdown |
| `_loginHandler` | `Func<byte[],Task<byte[]>>?` | User login logic |
| `_getHandlers` | `Dictionary<string,Func<byte[],Task<byte[]>>>` | REST GET handlers |
| `_postHandlers` | `Dictionary<string,Func<byte[],Task<byte[]>>>` | REST POST handlers |

### Constructor

```csharp
protected ServerCore(ServerConfig config)
```
1. Lưu `_config`
2. Tạo `_tokenService`, `_registry`, `_sessionMap`, `_handshakeHandler`, `_udpListener`
3. Link `_registry._sessionMap = _sessionMap`
4. Đăng ký `_handshakeHandler.OnUdpAuthRequest` vào raw TCP subject `"request_udp_auth"`
5. Khởi tạo `_getHandlers`, `_postHandlers`

### Public overrides (chuyển từ `ServerImplement`)

| Method | Mô tả |
|--------|-------|
| `Connections` | `→ _registry.GetAll()` |
| `OnConnect` | `→ _registry.OnConnect` |
| `OnDisconnect` | `→ _registry.OnDisconnect` |
| `CreateToken` | `→ _tokenService.CreateToken` |
| `GetConnectionById` | `→ _registry.GetById` |
| `SendOnTcp<T>` | Serialize → envelope → `conn.SendAsync` |
| `SendOnUdp<T>` | Serialize → envelope → `_udpListener.SendTo` |
| `SendAllOnTcp<T>` | Serialize once, iterate `Connected` connections |
| `SendAllOnUdp<T>` | Serialize once, iterate connections with `UdpAddress` |
| `SubscribeTcp<T>` | `→ _registry.SubscribeTcpLocal` |
| `SubscribeUdp<T>` | `→ _registry.SubscribeUdpLocal` |
| `OnWarning` | `→ _registry.OnWarning` |
| `OnLogin<T>` | Wrap auth → `_tokenService.CreateToken` → `LoginResponse` bytes |
| `OnGetRequest<T>` | Store in `_getHandlers` |
| `OnPostRequest<T,R>` | Store in `_postHandlers` |

### Protected methods

| Method | Mô tả |
|--------|-------|
| `HandleRestRequest(ApiRequest) → Task<ApiResponse>` | Chuyển từ private `ServerImplement`. Token validate (trừ `__login__`), decompress, dispatch `_loginHandler`/`_getHandlers`/`_postHandlers` |
| `HandleWebSocketConnection(WebSocket, IUser, CancellationToken) → Task` | Tạo `ConnectionImplement` → `_registry.Register` → `ReceiveLoop` → `_registry.Remove` |
| `static ReceiveLoop(ConnectionImplement, WebSocket, ConnectionRegistry, CancellationToken) → Task` | Extract từ `WebSocketListener.ReceiveLoop`. 64KB buffer, multi-frame assembly, `MessageEnvelope` parse, `_registry.Route` |
| `static Decompress(byte[]) → byte[]` | DeflateStream decompression |

### Subclass contract (abstract)

| Method | Mô tả |
|--------|-------|
| `protected abstract Task StartTransportAsync(CancellationToken ct)` | Bắt đầu accept connection |
| `protected abstract Task StopTransportAsync()` | Dừng accept, đợi connection hiện tại đóng |

### `DisposeAsync`

```csharp
override async ValueTask DisposeAsync() {
    _cts.Cancel();
    _udpListener.StopAsync();
    await StopTransportAsync();
    _cts.Dispose();
}
```

---

## Bước 2: `ServerImplement` — thin transport layer

`src/impl/ServerImplement.cs`, `#if NET9_0`, kế thừa `ServerCore`

Giảm từ ~235 dòng xuống ~60 dòng.

### Fields

| Field | Mô tả |
|-------|-------|
| `WebSocketListener _listener` | Raw TcpListener transport |

### Constructor

```csharp
private ServerImplement(ServerConfig config) : base(config)
{
    _listener = new WebSocketListener(config, _tokenService, _registry, HandleRestRequest);
}
```

### `Create`

```csharp
public static new IServer Create(ServerConfig config)
{
    var server = new ServerImplement(config);
    _ = server.StartTransportAsync(server._cts.Token);  // fire-and-forget
    _ = server._udpListener.StartAsync(config.udpPort, server._cts.Token);
    return server;
}
```

### `StartTransportAsync`

```csharp
protected override Task StartTransportAsync(CancellationToken ct)
    => _listener.StartAsync(ct);
```

### `StopTransportAsync`

```csharp
protected override async Task StopTransportAsync()
    => await _listener.StopAsync();
```

### Property

```csharp
public int WebSocketPort => _listener.Port;
```

---

## Bước 3: `ServerKestrel` — Kestrel transport

`src/impl/ServerKestrel.cs`, `#if NET9_0`, kế thừa `ServerCore`

~120 dòng.

### Fields

| Field | Mô tả |
|-------|-------|
| `WebApplication? _app` | ASP.NET Core app |
| `ServerKestrelConfig _kestrelConfig` | Config với KestrelUrls |

### Constructor

```csharp
private ServerKestrel(ServerKestrelConfig config) : base(config)
{
    _kestrelConfig = config;
}
```

### `Create`

```csharp
public static new IServer Create(ServerKestrelConfig config)
{
    var server = new ServerKestrel(config);
    _ = server.StartTransportAsync(server._cts.Token);  // fire-and-forget
    _ = server._udpListener.StartAsync(config.udpPort, server._cts.Token);
    return server;
}
```

### `StartTransportAsync`

```csharp
protected override async Task StartTransportAsync(CancellationToken ct)
{
    var builder = WebApplication.CreateBuilder([]);
    builder.WebHost.UseUrls(_kestrelConfig.KestrelUrls);

    var app = builder.Build();
    app.UseWebSockets();

    // WebSocket endpoint
    app.Map(_kestrelConfig.websocketEndpoint, HandleWebSocketAsync);

    // REST endpoint
    app.MapPost(_kestrelConfig.restEndpoint, HandleRestAsync);

    _app = app;
    await app.RunAsync(ct);
}
```

### `HandleWebSocketAsync`

```csharp
async Task HandleWebSocketAsync(HttpContext ctx)
{
    // 1. Parse token từ Authorization header
    var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer "))
    {
        ctx.Response.StatusCode = 401;
        return;
    }
    var token = auth["Bearer ".Length..].Trim();

    // 2. Validate JWT
    var principal = _tokenService.ValidateToken(token);
    if (principal == null)
    {
        ctx.Response.StatusCode = 401;
        return;
    }

    // 3. Trích xuất user từ claims
    var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
    var userName = principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "";
    var user = new JwtUser(userId, userName);
    // JwtUser là inner class của WebSocketListener → cần move ra hoặc define lại

    // 4. Accept WebSocket (Kestrel lo upgrade handshake)
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    // 5. Delegate shared connection logic
    await HandleWebSocketConnection(ws, user, _cts.Token);
}
```

### `HandleRestAsync`

```csharp
async Task HandleRestAsync(HttpContext ctx)
{
    // 1. Đọc body
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    // 2. Parse ApiRequest
    ApiRequest apiRequest;
    try { apiRequest = ApiRequest.Parser.ParseFrom(body); }
    catch
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    // 3. Dispatch (shared logic trong ServerCore)
    var response = await HandleRestRequest(apiRequest);

    // 4. Trả về
    var responseBytes = response.ToByteArray();
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.ContentLength = responseBytes.Length;
    await ctx.Response.Body.WriteAsync(responseBytes);
}
```

### `StopTransportAsync`

```csharp
protected override async Task StopTransportAsync()
{
    if (_app != null)
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
```

### `JwtUser`

Hiện `JwtUser` là inner class của `WebSocketListener`. Cần move ra thành class riêng hoặc vào `ServerCore` để cả 2 impl dùng chung.

**Quyết định**: Move `JwtUser` vào `ServerCore.cs` (hoặc `src/impl/JwtUser.cs` riêng).

---

## Bước 4: `WebSocketListener` — gọi `ServerCore.ReceiveLoop`

`src/impl/WebSocketListener.cs`

**Sửa `ReceiveLoop`** (hiện là private method tại line 184):

```csharp
// Trước:
async Task ReceiveLoop(ConnectionImplement conn, WebSocket ws) { ... }

// Sau:
Task ReceiveLoop(ConnectionImplement conn, WebSocket ws)
    => ServerCore.ReceiveLoop(conn, ws, _registry, _cts!.Token);
```

**Sửa `HandleWebSocketUpgrade`** (line 136):
- Sau khi tạo `ConnectionImplement` và `_registry.Register`, gọi `ServerCore.ReceiveLoop` thay vì `ReceiveLoop` nội bộ
- Sau `ReceiveLoop` kết thúc, `_registry.Remove(conn.Id)`

Tất cả phần còn lại của `WebSocketListener` giữ nguyên.

---

## Bước 5: `ServerKestrelConfig`

`src/impl/ServerKestrelConfig.cs`

```csharp
namespace MyConnection.Impl;

public class ServerKestrelConfig : ServerConfig
{
    /// <summary>
    /// Địa chỉ URL cho Kestrel lắng nghe.
    /// Mặc định: "http://0.0.0.0:9090"
    /// </summary>
    public string KestrelUrls { get; set; } = "http://0.0.0.0:9090";
}
```

**Ghi chú**: `tcpPort` từ `ServerConfig` không dùng trong Kestrel — port nằm trong `KestrelUrls`.

---

## Bước 6: DI Extension

`src/impl/KestrelHostedService.cs`

```csharp
#if NET9_0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MyConnection.Impl;

public static class MyConnectionServiceExtensions
{
    /// <summary>
    /// Đăng ký MyConnection server vào ASP.NET Core DI container.
    /// Server tự động start cùng app và stop khi app shutdown.
    /// </summary>
    public static IServiceCollection AddMyConnectionServer(
        this IServiceCollection services,
        ServerKestrelConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton<IServer>(sp =>
        {
            var cfg = sp.GetRequiredService<ServerKestrelConfig>();
            return ServerKestrel.Create(cfg);
        });
        services.AddHostedService<KestrelHostedService>();
        return services;
    }

    /// <summary>
    /// Đăng ký MyConnection server với config từ IConfiguration.
    /// </summary>
    public static IServiceCollection AddMyConnectionServer(
        this IServiceCollection services,
        Action<ServerKestrelConfig> configure)
    {
        var config = new ServerKestrelConfig();
        configure(config);
        return services.AddMyConnectionServer(config);
    }
}

internal class KestrelHostedService : IHostedService
{
    private readonly IServer _server;

    public KestrelHostedService(IServer server) => _server = server;

    public Task StartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask; // Server đã start trong ServerKestrel.Create

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _server.DisposeAsync();
}
#endif
```

---

## Bước 7: `IServer.Create` — tự chọn impl

`src/IServer.cs` — chỉ sửa static factory method:

```csharp
// Trước:
#if NET9_0
    static IServer Create(ServerConfig config) => ServerImplement.Create(config);
#else
    static IServer Create(ServerConfig config) => throw new PlatformNotSupportedException();
#endif

// Sau:
#if NET9_0
    static IServer Create(ServerConfig config)
    {
        if (config is ServerKestrelConfig kc)
            return ServerKestrel.Create(kc);
        return ServerImplement.Create(config);
    }
#else
    static IServer Create(ServerConfig config)
        => throw new PlatformNotSupportedException("Server yêu cầu .NET 9.0+");
#endif
```

Người dùng chỉ cần truyền đúng config type, không cần biết impl bên trong.

---

## Bước 8: `MyConnection.csproj`

```xml
<!-- Thêm vào ItemGroup cho net9.0 -->
<ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
```

`Microsoft.AspNetCore.App` là shared framework — khi publish framework-dependent không tăng size. Khi publish self-contained, tăng ~50MB.

---

## Bước 9: Test

Thêm test class mới hoặc mở rộng `ConnectionTests.cs`:

### Test cases cho `ServerKestrel`

| Test | Mô tả | Assert |
|------|-------|--------|
| `KestrelServer_Create_Starts` | Tạo `ServerKestrel`, gọi REST ping | Nhận `ApiResponse` |
| `KestrelServer_WebSocket_Upgrade` | Client kết nối WebSocket | `IsConnected == true` |
| `KestrelServer_Tcp_Message` | Gửi/nhận message TCP qua WS | Nhận đúng data |
| `KestrelServer_Udp_Handshake` | UDP handshake thành công | `UdpAddress != ""` |
| `KestrelServer_Udp_Message` | Gửi/nhận message UDP | Nhận đúng data |
| `KestrelServer_LoginFlow` | `OnLogin` handler → token → connect | `Connection.User.Name` đúng |
| `KestrelServer_GetRequest` | `OnGetRequest` handler | Nhận `ApiResponse` đúng |
| `KestrelServer_PostRequest` | `OnPostRequest` handler | Nhận `ApiResponse` đúng |
| `KestrelServer_TokenExpired` | Token sai → 401 | Không kết nối được WS |
| `KestrelServer_DisposeAsync` | Dispose server | Client mất kết nối |

### Test setup

```csharp
private async Task<(ServerKestrel, TestClient)> CreateKestrelPair()
{
    var config = new ServerKestrelConfig
    {
        KestrelUrls = "http://127.0.0.1:0",  // port 0 = OS chọn
        websocketEndpoint = "/ws",
        restEndpoint = "/api",
        udpPort = TestHelper.NextUdpPort(),
        jwtSecret = "test-secret-key-at-least-32-bytes!!",
        jwtIssuer = "test",
        jwtAudience = "test"
    };
    var server = ServerKestrel.Create(config);

    // Đợi Kestrel start và lấy actual port
    // Cần mechanism để lấy port sau khi Kestrel bind
    // Option: expose WebSocketPort property trên ServerKestrel
    // hoặc đọc HttpApplication.ServerFeatures...

    var client = new TestClient(new ClientConfig
    {
        tcpServer = $"127.0.0.1:{port}",
        // ...
    });

    await client.SetToken("test-token");
    return (server, client);
}
```

### Lưu ý test Kestrel

1. **Port 0 binding**: Kestrel hỗ trợ `http://127.0.0.1:0` → OS chọn port tự do → cần expose port thực tế qua `ServerKestrel.Port` property
2. **WebSocket URL**: Kestrel mặc định dùng ws:// cho WebSocket upgrade qua HTTP listener
3. **UDP port vẫn riêng**: Test cần port UDP không đụng độ (dùng helper chọn port tự do)

---

## Bước 10: `GUIDE.md` — bổ sung

Thêm section mới giữa Section 2 và Section 3 hiện tại:

```markdown
## 2B. Server Kestrel (ASP.NET Core)

### 2B.1 Tạo standalone

```csharp
var config = new ServerKestrelConfig
{
    KestrelUrls = "http://0.0.0.0:9090",
    websocketEndpoint = "/ws",
    restEndpoint = "/api",
    jwtSecret = "your-secret-at-least-32-bytes!!"
};

IServer server = IServer.Create(config);
// Server tự start, nghe trên port 9090 qua Kestrel
```

### 2B.2 Tích hợp ASP.NET Core DI

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMyConnectionServer(cfg =>
{
    cfg.KestrelUrls = "http://0.0.0.0:9090";
    cfg.websocketEndpoint = "/ws";
    cfg.restEndpoint = "/api";
    cfg.jwtSecret = "your-secret-at-least-32-bytes!!";
});

var app = builder.Build();

// Lấy IServer từ DI để đăng ký handler
var server = app.Services.GetRequiredService<IServer>();
server.OnLogin<LoginRequest>(async data => { ... });
server.SubscribeTcp<ChatMessage>("chat", (conn, msg) => { ... });

app.Run();
```

### 2B.3 Khác biệt với ServerImplement

| Tính năng | ServerImplement | ServerKestrel |
|-----------|----------------|---------------|
| Transport | Raw TcpListener | Kestrel |
| HTTP parsing | Thủ công | ASP.NET Core pipeline |
| WebSocket upgrade | RFC 6455 viết tay | Kestrel middleware |
| Keep-alive REST | Connection: close | Có |
| TLS/HTTPS | Không | Sẵn (cấu hình cert) |
| Middleware | Không | CORS, logging, rate limit... |
| Health checks | Không | app.MapHealthChecks() |
| Kích thước publish | Nhẹ | +50MB (self-contained) |
| Target | net9.0 + netstandard2.1 | net9.0 only |
```

---

## Kiến trúc tổng thể sau thay đổi

```
IServer (interface + static factory)
└── ServerAbstract (abstract, tất cả abstract members)
    └── ServerCore (#if NET9_0, shared logic)
        ├── ServerImplement (raw TcpListener)
        │   └── WebSocketListener
        └── ServerKestrel (ASP.NET Core Kestrel)
            └── WebApplication (built-in)

IClient (interface + static factory)
└── ClientAbstract
    └── ClientImplement

Shared:
├── ConnectionRegistry (routing, tracking)
├── ConnectionImplement (IConnection wrapper)
├── ServerTokenService (JWT)
├── UdpListener, UdpSessionMap, UdpHandshakeHandler
├── ProtoSerializer, MessageEnvelope, Api*, etc.
```

---

## Cách dùng của người dùng cuối

```csharp
// === Standalone (raw socket) ===
var server1 = IServer.Create(new ServerConfig
{
    tcpPort = 9090,
    jwtSecret = "..."
});

// === Kestrel standalone ===
var server2 = IServer.Create(new ServerKestrelConfig
{
    KestrelUrls = "http://0.0.0.0:9090",
    jwtSecret = "..."
});

// === Tích hợp ASP.NET Core ===
builder.Services.AddMyConnectionServer(cfg => { ... });
```

---

## Risk & lưu ý

| Risk | Mức | Giải pháp |
|------|-----|-----------|
| `Microsoft.AspNetCore.App` tăng publish size | LOW | Dùng framework-dependent publish (không self-contained) |
| `JwtUser` là inner class của `WebSocketListener` | MEDIUM | Move ra `ServerCore` hoặc file riêng |
| `ServerImplement.Create` hiện trả `ServerImplement`, sửa thành trả `IServer` | LOW | Đã trả `IServer` từ `IServer.Create`, chỉ cần kiểm tra gọi trực tiếp `ServerImplement.Create` |
| Test Kestrel cần port động | LOW | Dùng `http://127.0.0.1:0`, expose `Port` property |
| `ReceiveLoop` dùng `_registry` internal | LOW | Chuyển `_registry` thành protected field trong `ServerCore` |
| Build warning khi `ServerImplement.Port` và `ServerKestrel.Port` cùng tên | LOW | Giữ `WebSocketPort` trên `ServerImplement`, thêm `Port` trên `ServerKestrel` |

---

## Thứ tự triển khai

1. **`ServerCore.cs`** — Extract shared logic từ `ServerImplement`, chạy test cũ xác nhận không break
2. **Sửa `ServerImplement.cs`** — Kế thừa `ServerCore`, chỉ giữ transport code
3. **Sửa `WebSocketListener.cs`** — `ReceiveLoop` gọi static method từ `ServerCore`
4. **Move `JwtUser`** — Từ `WebSocketListener` inner class ra file riêng hoặc vào `ServerCore`
5. **`ServerKestrelConfig.cs`** — Config đơn giản, 1 field mới
6. **`ServerKestrel.cs`** — Triển khai chính, build + run WebApplication
7. **Sửa `IServer.cs`** — `Create()` tự chọn impl
8. **Sửa `MyConnection.csproj`** — Thêm `FrameworkReference`
9. **`KestrelHostedService.cs`** — DI extension
10. **Test** — Xác nhận tất cả flow hoạt động trên Kestrel
11. **`GUIDE.md`** — Cập nhật tài liệu
12. **Build final** — `net9.0` + `netstandard2.1`, 0 error 0 warning

---

## Update: Refactor tích hợp WebApplication host (ĐÃ TRIỂN KHAI)

Sau khi hoàn thành 10 bước trên, phát hiện **double Kestrel** khi dùng DI với `WebApplication.CreateBuilder`. Đã refactor theo plan `plan/kestrel-integration.md`.

### Thay đổi so với plan gốc

| Mục | Plan gốc | Sau refactor |
|-----|----------|-------------|
| `ServerKestrel` constructor | `private` | **`internal`** (để DI factory gọi) |
| `Create()` | Tạo `WebApplication` trong `StartTransportAsync` | **Tách riêng**: tạo `WebApplication` → `ConfigureWebApplication(app)` → `StartTransportAsync` |
| `StartTransportAsync` | Luôn tạo + chạy Kestrel | **Có điều kiện**: chỉ chạy nếu `_ownsApp == true` |
| `StopTransportAsync` | Luôn stop + dispose Kestrel | **Có điều kiện**: chỉ stop nếu `_ownsApp == true` |
| DI factory | `ServerKestrel.Create(cfg)` (start ngay) | **`new ServerKestrel(cfg)`** (không start, constructor internal) |
| `UseMyConnectionServer` | Không có | **Mới**: extension trên `WebApplication` |
| `ConsoleDemo` | `Host.CreateDefaultBuilder` | **`WebApplication.CreateBuilder`** + `app.UseMyConnectionServer()` + `app.RunAsync()` |

### File mới/thay đổi

| # | File | Thao tác |
|---|------|----------|
| — | `src/impl/ServerKestrel.cs` | **SỬA**: `_ownsApp`, `ConfigureWebApplication`, `StartUdpAsync`, `StopUdpAsync`, constructor `internal` |
| — | `src/impl/KestrelHostedService.cs` | **SỬA**: Factory dùng `new ServerKestrel(cfg)` |
| — | `src/impl/MyConnectionWebApplicationExtensions.cs` | **MỚI**: `UseMyConnectionServer(this WebApplication)` |
| — | `ConsoleDemo/Program.cs` | **SỬA**: `WebApplication.CreateBuilder` + `UseMyConnectionServer` |

Xem chi tiết tại `plan/kestrel-integration.md`.
