# Plan: ServerKestrel tích hợp WebApplication host (ĐÃ TRIỂN KHAI)

## Trạng thái: Hoàn thành

Build: **0 error 0 warning**, Test: **13/13 passed**

---

## Vấn đề

`ServerKestrel.Create()` luôn tạo một `WebApplication` nội bộ. Khi dùng DI với `WebApplication.CreateBuilder` ở host, kết quả có **2 Kestrel**: một của host (port 5000, không route) + một nội bộ (port 9090, có route).

## Mục tiêu

- **1 Kestrel duy nhất** khi dùng DI: ServerKestrel dùng chung `WebApplication` pipeline của host
- **Standalone vẫn hoạt động**: `IServer.Create(new ServerKestrelConfig {...})` tự tạo `WebApplication` nội bộ
- **Không break `ServerImplement`** và test hiện tại

---

## Thiết kế

```
Standalone path:                       DI path:
─────────────────                      ───────
IServer.Create(config)                 builder.Services.AddMyConnectionServer(config)
  └─ ServerKestrel.Create(config)      var app = builder.Build();
       ├─ new ServerKestrel(config)    app.UseMyConnectionServer();
       ├─ Tạo WebApplication nội bộ         ├─ server.ConfigureWebApplication(app)
       ├─ ConfigureWebApplication(app)      ├─ server.StartUdpAsync()
       ├─ StartTransportAsync (block)       └─ (Kestrel do host app.RunAsync() quản lý)
       └─ StartUdpAsync()
                                       await app.RunAsync();
```

### Nguyên lý cốt lõi

`ServerKestrel(ServerKestrelConfig)` constructor **không tạo WebApplication**, **không start transport**, **không tạo UDP**. Thay vào đó:

- **Standalone**: `Create()` tạo WebApplication nội bộ, gọi `ConfigureWebApplication(app)`, đánh dấu `_ownsApp = true`, rồi fire-and-forget `StartTransportAsync` + `StartUdpAsync`
- **DI**: Factory chỉ `new ServerKestrel(cfg)`. Sau `builder.Build()`, extension `app.UseMyConnectionServer()` gọi `ConfigureWebApplication(app)` + `StartUdpAsync()`. Kestrel do `WebApplication.RunAsync()` của host quản lý

### `StartTransportAsync` / `StopTransportAsync` semantics

| | `_ownsApp == true` (standalone) | `_ownsApp == false` (DI) |
|---|---|---|
| `StartTransportAsync` | `app.StartAsync(ct)` + `app.WaitForShutdownAsync(ct)` (block) | `ValueTask.CompletedTask` (no-op) |
| `StopTransportAsync` | `app.StopAsync()` + `app.DisposeAsync()` | `ValueTask.CompletedTask` (no-op) |

---

## Các file đã thay đổi (4 file, 1 mới)

| # | File | Thao tác | Chi tiết |
|---|------|----------|----------|
| 1 | `src/impl/ServerKestrel.cs` | **SỬA** | Tách constructor, thêm `_ownsApp`, `ConfigureWebApplication`, `StartUdpAsync`, `StopUdpAsync` |
| 2 | `src/impl/KestrelHostedService.cs` | **SỬA** | Factory dùng `new ServerKestrel(cfg)` thay vì `ServerKestrel.Create(cfg)` |
| 3 | `src/impl/MyConnectionWebApplicationExtensions.cs` | **MỚI** | Extension `UseMyConnectionServer(this WebApplication app)` |
| 4 | `ConsoleDemo/Program.cs` | **SỬA** | `WebApplication.CreateBuilder` + `app.UseMyConnectionServer()` + `app.RunAsync()` |

---

## Triển khai chi tiết (code thực tế)

### 1. `src/impl/ServerKestrel.cs` (122 dòng)

```csharp
#if NET9_0
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;          // WaitForShutdownAsync extension
using System.Net.WebSockets;

namespace MyConnection;

public class ServerKestrel : ServerCore
{
    private WebApplication? _app;
    private bool _ownsApp;                   // true = standalone tự tạo WebApplication
    private readonly ServerKestrelConfig _kestrelConfig;

    // Constructor internal — DI factory gọi được, không side-effect
    internal ServerKestrel(ServerKestrelConfig config) : base(config)
    {
        _kestrelConfig = config;
    }

    // Standalone: tạo WebApplication nội bộ, start transport + UDP, trả IServer
    public static IServer Create(ServerKestrelConfig config)
    {
        var server = new ServerKestrel(config);
        var builder = WebApplication.CreateBuilder(new string[0]);
        builder.WebHost.UseUrls(config.KestrelUrls);
        var app = builder.Build();
        server.ConfigureWebApplication(app);
        server._ownsApp = true;              // đánh dấu standalone → Start/StopTransport will run Kestrel
        _ = server.StartTransportAsync(server._cts.Token);
        server.StartUdpAsync();
        return server;
    }

    // DI: đăng ký WebSocket + REST middleware vào WebApplication của host
    public void ConfigureWebApplication(WebApplication app)
    {
        app.UseWebSockets();
        app.Map(_kestrelConfig.websocketEndpoint, HandleWebSocketAsync);
        app.MapPost(_kestrelConfig.restEndpoint, HandleRestAsync);
        _app = app;
    }

    // Start UDP listener (dùng chung cho cả 2 path)
    public void StartUdpAsync()
    {
        _udpListener = new UdpListener(_sessionMap, _registry);
        _ = _udpListener.StartAsync(_config.udpPort, _cts.Token);
    }

    // Stop UDP listener
    public void StopUdpAsync()
    {
        _udpListener?.StopAsync();
    }

    // Start transport: nếu standalone thì chạy Kestrel, DI thì no-op
    protected override async ValueTask StartTransportAsync(CancellationToken ct)
    {
        if (_ownsApp && _app != null)
        {
            await _app.StartAsync(ct);
            await _app.WaitForShutdownAsync(ct);  // block đến khi shutdown
        }
    }

    // ... HandleWebSocketAsync, HandleRestAsync giữ nguyên ...

    // Stop transport: nếu standalone thì stop + dispose Kestrel, DI thì no-op
    protected override async ValueTask StopTransportAsync()
    {
        if (_ownsApp && _app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
#endif
```

### 2. `src/impl/KestrelHostedService.cs` (45 dòng)

```csharp
public static IServiceCollection AddMyConnectionServer(
    this IServiceCollection services, ServerKestrelConfig config)
{
    services.AddSingleton(config);
    services.AddSingleton<IServer>(sp =>
    {
        var cfg = sp.GetRequiredService<ServerKestrelConfig>();
        return new ServerKestrel(cfg);       // constructor internal, không start transport
    });
    services.AddHostedService<KestrelHostedService>();
    return services;
}
```

> `KestrelHostedService.StartAsync` trả về `Task.CompletedTask` — UDP + endpoint đã được `UseMyConnectionServer` khởi tạo trước `app.RunAsync()`.
> `KestrelHostedService.StopAsync` gọi `await _server.DisposeAsync()` để cleanup.

### 3. `src/impl/MyConnectionWebApplicationExtensions.cs` (17 dòng) — MỚI

```csharp
#if NET9_0
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MyConnection;

public static class MyConnectionWebApplicationExtensions
{
    public static WebApplication UseMyConnectionServer(this WebApplication app)
    {
        var server = (ServerKestrel)app.Services.GetRequiredService<IServer>();
        server.ConfigureWebApplication(app);  // đăng ký WS + REST endpoint
        server.StartUdpAsync();               // start UDP listener
        return app;
    }
}
#endif
```

### 4. `ConsoleDemo/Program.cs` (221 dòng)

```csharp
var builder = WebApplication.CreateBuilder(args);  // ← dùng WebApplication, không còn Host
var config = new ServerKestrelConfig { KestrelUrls = "http://0.0.0.0:9090", ... };
builder.Services.AddMyConnectionServer(config);
var app = builder.Build();
var server = app.Services.GetRequiredService<IServer>();
// ... đăng ký handlers ...
app.UseMyConnectionServer();  // ← extension mới
await app.RunAsync();         // ← 1 Kestrel duy nhất trên port 9090
```

> `ConsoleDemo.csproj`: giữ nguyên `<FrameworkReference Include="Microsoft.AspNetCore.App"/>` (đã có từ lần trước).

---

## Cách dùng sau refactor

```csharp
// === Standalone ===
using var server = IServer.Create(new ServerKestrelConfig
{
    KestrelUrls = "http://0.0.0.0:9090",
    jwtSecret = "..."
});
server.OnLogin<StringValue>(...);
// Tự tạo WebApplication nội bộ, tự quản lý lifecycle, await using auto-cleanup

// === ASP.NET Core DI (ConsoleDemo pattern) ===
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMyConnectionServer(config);
var app = builder.Build();

var server = app.Services.GetRequiredService<IServer>();
server.OnLogin<StringValue>(...);
server.SubscribeTcp<StringValue>("echo", ...);

app.UseMyConnectionServer();  // Đăng ký endpoint + start UDP
await app.RunAsync();         // 1 Kestrel duy nhất
```

---

## Kiến trúc sau refactor

```
IServer.Create(ServerKestrelConfig)  ──→  ServerKestrel.Create(config)  [standalone]
  └─ Tạo WebApplication nội bộ, ConfigureWebApplication, StartUdpAsync, _ownsApp=true

builder.Services.AddMyConnectionServer(config)  ──→  DI container
  └─ AddSingleton<IServer>(sp => new ServerKestrel(cfg))  ← constructor internal, no side-effect
  └─ AddHostedService<KestrelHostedService>                ← chỉ quản lý DisposeAsync khi shutdown

app.UseMyConnectionServer()  ──→  MyConnectionWebApplicationExtensions
  ├─ server.ConfigureWebApplication(app)  ← đăng ký UseWebSockets, Map WS, MapPost REST
  └─ server.StartUdpAsync()               ← tạo UdpListener + StartAsync

app.RunAsync()  ──→  WebApplication.RunAsync  ← Kestrel duy nhất, lifecycle do host
```

---

## Kết quả build + test

```
dotnet build MyConnection.csproj            → 0 error, 0 warning (net9.0 + netstandard2.1)
dotnet build ConsoleDemo/ConsoleDemo.csproj  → 0 error, 0 warning
dotnet test MyConnection.Tests/              → 13 passed, 0 failed, 0 skipped
```

---

## Risk đã xử lý

| Risk | Mức | Kết quả |
|------|-----|---------|
| Constructor `private`→`internal` | LOW | `ServerImplement` không gọi constructor trực tiếp, không break |
| `Create()` standalone signature giữ nguyên | LOW | `IServer.Create(config)` contract không đổi |
| `DisposeAsync` order: `_cts.Cancel()` → UDP → Kestrel | LOW | Giữ nguyên order trong `ServerCore.DisposeAsync` |
| `_ownsApp = false` → `StopTransportAsync()` no-op | LOW | Host đã dừng Kestrel trước khi gọi `DisposeAsync` |
| `WaitForShutdownAsync` cần `using Microsoft.Extensions.Hosting` | FIXED | Đã thêm import |
