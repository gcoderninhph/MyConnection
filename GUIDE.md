# MyConnection — Hướng Dẫn Sử Dụng

## 1. Cài đặt

Thêm `MyConnection.csproj` vào solution, hoặc cài từ NuGet local:

```bash
dotnet add reference path/to/MyConnection.csproj
```

```xml
<!-- NuGet local package -->
<PackageReference Include="MyConnection" Version="1.0.2" />
```

Server yêu cầu `.NET 9.0`. Client hỗ trợ cả `.NET 9.0` và `netstandard2.1` (Unity).

---

## 2. Server

### 2.1 Tạo & cấu hình

```csharp
using MyConnection;

var config = new ServerConfig
{
    tcpPort       = 9090,
    websocketEndpoint = "/ws",
    restEndpoint  = "/api",
    udpPort       = 9091,
    jwtSecret     = "your-secret-key-at-least-32-bytes!!",
    jwtIssuer     = "my-app",
    jwtAudience   = "my-app"
};

var server = (ServerImplement)IServer.Create(config);
// Server tự động bắt đầu lắng nghe TCP + UDP
```

#### ServerKestrel standalone (ASP.NET Core Kestrel nội bộ)

```csharp
var kestrelConfig = new ServerKestrelConfig
{
    KestrelUrls = "http://0.0.0.0:9090",
    websocketEndpoint = "/ws",
    restEndpoint = "/api",
    udpPort = 9091,
    jwtSecret = "your-secret-key-at-least-32-bytes!!",
    jwtIssuer = "my-app",
    jwtAudience = "my-app"
};

await using var server = (ServerKestrel)IServer.Create(kestrelConfig);
// Server tự động tạo WebApplication nội bộ, quản lý Kestrel lifecycle
```

#### ServerKestrel DI (shared WebApplication pipeline)

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMyConnectionServer(new ServerKestrelConfig
{
    KestrelUrls = "http://0.0.0.0:9090",
    websocketEndpoint = "/ws",
    restEndpoint = "/api",
    udpPort = 9091,
    jwtSecret = "your-secret-key-at-least-32-bytes!!",
    jwtIssuer = "my-app",
    jwtAudience = "my-app"
});

var app = builder.Build();
var server = app.Services.GetRequiredService<IServer>();

// Đăng ký handlers...
server.OnLogin<LoginRequest>(async data => { ... });
server.OnConnect(conn => Console.WriteLine($"[+] {conn.User.Name}"));

app.UseMyConnectionServer();
await app.RunAsync(); // 1 Kestrel duy nhất, shared pipeline
```

> **So sánh**: `ServerImplement` dùng raw `TcpListener` — nhẹ nhất. `ServerKestrel` standalone tự tạo WebApplication — tiện cho app nhỏ. `ServerKestrel` DI dùng chung WebApplication — phù hợp khi bạn có sẵn ASP.NET Core app.

### 2.2 Xác thực đăng nhập

```csharp
// Định nghĩa message login (Protobuf IMessage<T>)
// Đã có sẵn StringValue trong library, hoặc bạn tự tạo proto riêng

server.OnLogin<LoginRequest>(async data =>
{
    // data là LoginRequest đã deserialize từ Protobuf
    if (data.Username == "admin" && data.Password == "123456")
    {
        // Trả về IUser
        return new UserInfo("1", "Admin");
    }
    throw new Exception("Sai tên đăng nhập hoặc mật khẩu");
});

// UserInfo là triển khai đơn giản của IUser
record UserInfo(string Id, string Name) : IUser;
```

### 2.3 Sự kiện kết nối

```csharp
server.OnConnect(conn =>
{
    Console.WriteLine($"[+] {conn.User.Name} kết nối (ID: {conn.Id})");

    // Gửi chào mừng
    server.SendOnTcp("welcome", conn, new StringValue { Value = "Chào mừng!" });
});

server.OnDisconnect(conn =>
{
    Console.WriteLine($"[-] {conn.User.Name} ngắt kết nối (ID: {conn.Id})");
});
```

### 2.4 Nhận message từ client

```csharp
// Đăng ký nhận message TCP
server.SubscribeTcp<ChatMessage>("chat", (conn, msg) =>
{
    Console.WriteLine($"{conn.User.Name} gửi: {msg.Text}");

    // Phản hồi riêng
    server.SendOnTcp("chat_response", conn, new StringValue { Value = "Đã nhận" });

    // Hoặc broadcast tới tất cả (trừ người gửi)
    server.SendAllOnTcp("chat", msg);
});

// Đăng ký nhận message UDP
server.SubscribeUdp<Position>("move", (conn, pos) =>
{
    // Broadcast vị trí tới tất cả client khác
    server.SendAllOnUdp("move", pos);
});
```

### 2.5 Broadcast

```csharp
// Gửi tới tất cả client qua TCP
server.SendAllOnTcp("announce", new StringValue { Value = "Server sắp bảo trì" });

// Gửi tới tất cả client qua UDP
server.SendAllOnUdp("announce", new StringValue { Value = "Server sắp bảo trì" });
```

### 2.6 Gửi tới client cụ thể

```csharp
var conn = server.GetConnectionById("some-connection-id");
if (conn != null)
{
    server.SendOnTcp("private_msg", conn, new StringValue { Value = "Tin nhắn riêng" });
    server.SendOnUdp("position_sync", conn, new Position { X = 10, Y = 20 });
}
```

### 2.7 Subscribe connection (chat room)

Khi muốn route message từ A sang B theo subject mà không cần server xử lý logic:

```csharp
server.OnConnect(conn =>
{
    // Cho client này tham gia room "lobby"
    server.SubscribeConnection(conn.Id, "lobby");
});

// Bây giờ mọi client gửi subject "lobby" sẽ tự động route tới tất cả client trong room đó
```

### 2.8 REST API handler

```csharp
// GET: client gọi GetRequest<StringValue>("ping")
// Handler nhận IUser làm tham số đầu tiên
server.OnGetRequest<StringValue>("ping", user =>
{
    Console.WriteLine($"[{user.Name}] gọi ping");
    return Task.FromResult(new StringValue { Value = "pong" });
});

// POST: client gọi PostRequest<EchoRequest, EchoResponse>("echo", body)
server.OnPostRequest<StringValue, StringValue>("echo", (user, req) =>
{
    Console.WriteLine($"[{user.Name}] Echo: {req.Value}");
    return Task.FromResult(req); // Trả lại nguyên dữ liệu
});

// POST có kiểm tra dữ liệu
server.OnPostRequest<CreateItemRequest, CreateItemResponse>("create_item", async (user, req) =>
{
    // Validate + lưu DB...
    return new CreateItemResponse { Id = Guid.NewGuid().ToString(), Name = req.Name };
});
```

### 2.9 Cảnh báo server

```csharp
server.OnWarning(w =>
{
    if (w.Connection != null)
        Console.WriteLine($"[WARN] {w.Code}: {w.Message} (Client: {w.Connection.User.Name})");
    else
        Console.WriteLine($"[WARN] {w.Code}: {w.Message}");
});
```

### 2.10 Tạo token

```csharp
// Tạo JWT token để client dùng kết nối
var token = server.CreateToken("user123", "PlayerName");
// Gửi token này cho client qua cách khác (HTTP response login của bạn, email, v.v.)
```

### 2.11 Dừng server

```csharp
await server.DisposeAsync();
```

---

## 3. Client

### 3.1 Tạo & cấu hình

```csharp
using MyConnection;

var config = new ClientConfig
{
    tcpServer           = "127.0.0.1:9090",
    websocketEnpoint    = "/ws",
    restEndpoint        = "/api",
    udpServer           = "127.0.0.1:9091",  // Để "" nếu không cần UDP
    restCompressedEnable = false,
    tcpSecurity         = false,              // true nếu dùng wss://
    udpPingIntervalMs   = 5000,
    udpPingTimeoutMs    = 15000
};

var client = IClient.Create(config);
```

### 3.2 Đăng nhập & kết nối

```csharp
// Cách 1: Login data đồng bộ
var user = await client.Login(() => new LoginRequest
{
    Username = "admin",
    Password = "123456"
});

// Cách 2: Login data bất đồng bộ (gọi API, đọc file, etc.)
var user = await client.Login(async () =>
{
    var data = await LoadCredentialsFromStorage();
    return new LoginRequest { Username = data.User, Password = data.Pass };
});

Console.WriteLine($"Đăng nhập thành công: {user.Name} (ID: {user.Id})");

// Kết nối WebSocket + UDP
await client.ConnectServer();
Console.WriteLine($"Đã kết nối: {client.IsConnected}");
```

### 3.3 Gửi message

```csharp
// Gửi qua TCP (tin cậy, có thứ tự)
client.SendOnTcp("chat", new ChatMessage { Text = "Hello!", Sender = "me" });

// Gửi qua UDP (nhanh, không đảm bảo thứ tự)
client.SendOnUdp("move", new Position { X = 10.5f, Y = 20.3f });
```

### 3.4 Nhận message

```csharp
// Nhận message TCP
client.SubscribeTcp<ChatMessage>("chat", msg =>
{
    Console.WriteLine($"{msg.Sender}: {msg.Text}");
});

// Nhận message UDP
client.SubscribeUdp<Position>("move", pos =>
{
    UpdatePlayerPosition(pos.X, pos.Y);
});

// Hủy đăng ký
var sub = client.SubscribeTcp<StringValue>("temp", msg => { });
// Sau này:
sub.UnSubscribe();
```

### 3.5 Sự kiện kết nối

```csharp
// Khi WebSocket bị đóng (server tắt, mạng mất)
client.OnDisconnect(() =>
{
    Console.WriteLine("Mất kết nối tới server");
    // Có thể tự động reconnect
    _ = TryReconnect();
});

async Task TryReconnect()
{
    await Task.Delay(2000);
    try
    {
        await client.ConnectServer();
        Console.WriteLine("Đã kết nối lại");
    }
    catch (ConnectionFailedException)
    {
        Console.WriteLine("Không thể kết nối lại, thử sau...");
    }
}
```

### 3.6 Cảnh báo

```csharp
client.OnWarning(w =>
{
    switch (w.Code)
    {
        case "W001": Console.WriteLine($"Ping timeout: {w.Message}"); break;
        case "W003": Console.WriteLine($"Chưa kết nối: {w.Message}"); break;
        case "W005": Console.WriteLine($"WebSocket đóng: {w.Message}"); break;
        case "W006": Console.WriteLine($"Rớt tin TCP: {w.Message}"); break;
        case "W007": Console.WriteLine($"Rớt tin UDP: {w.Message}"); break;
    }
});
```

### 3.7 REST API

```csharp
// GET request (không payload)
var pingResult = await client.GetRequest<StringValue>("ping");
Console.WriteLine(pingResult.Value); // "pong"

// POST request (có payload)
var echoResult = await client.PostRequest<StringValue, StringValue>("echo",
    new StringValue { Value = "hello" });
Console.WriteLine(echoResult.Value); // "hello"

// POST với nén payload (nếu restCompressedEnable = true)
var bigData = await client.PostRequest<BigRequest, BigResponse>("process", request);
```

### 3.8 Đo RTT

```csharp
// Độ trễ mạng (ms), chỉ hoạt động khi UDP được bật
if (client.LatestRttMs.HasValue)
    Console.WriteLine($"Ping: {client.LatestRttMs.Value}ms");

// Kiểm tra trạng thái kết nối
if (client.IsConnected)
    Console.WriteLine("Đang kết nối");
```

### 3.9 Ngắt kết nối & Reconnect

```csharp
// Ngắt kết nối, token vẫn giữ → có thể kết nối lại
await client.DisconnectAsync();

// Kết nối lại, không cần login lại
await client.ConnectServer();
```

### 3.10 Đăng xuất hoàn toàn

```csharp
// Xóa token + disconnect → cần login lại nếu muốn dùng tiếp
await client.Logout();

// Đăng nhập lại với tài khoản khác
await client.Login(() => new LoginRequest { Username = "user2", Password = "pass2" });
await client.ConnectServer();
```

### 3.11 Dọn dẹp

```csharp
// Cách 1: await using (khuyên dùng)
await using (var client = IClient.Create(config))
{
    await client.Login(() => new LoginRequest { Username = "admin", Password = "123" });
    await client.ConnectServer();
    // ... sử dụng ...
} // Tự động gọi DisposeAsync → Logout → cleanup

// Cách 2: Gọi thủ công
await client.DisposeAsync();
```

---

## 4. Protobuf — Tạo message riêng

### 4.1 Tạo file `.proto`

```protobuf
syntax = "proto3";
package myapp;
option csharp_namespace = "MyGame";

message LoginRequest {
  string username = 1;
  string password = 2;
}

message ChatMessage {
  string text = 1;
  string sender = 2;
}

message Position {
  float x = 1;
  float y = 2;
  float z = 3;
}
```

### 4.2 Thêm vào project

```xml
<ItemGroup>
  <Protobuf Include="protos\*.proto" />
</ItemGroup>
```

### 4.3 Sử dụng

```csharp
var msg = new ChatMessage { Text = "Hello", Sender = "Player1" };
client.SendOnTcp("chat", msg);
client.SendOnUdp("chat", msg);
```

---

## 5. Kịch bản phổ biến

### 5.1 Chat đơn giản

```csharp
// === SERVER ===
server.SubscribeTcp<ChatMessage>("chat", (conn, msg) =>
{
    server.SendAllOnTcp("chat", msg);
});

// === CLIENT ===
await client.Login(() => new LoginRequest { Username = "Player1", Password = "x" });
await client.ConnectServer();

client.SubscribeTcp<ChatMessage>("chat", msg =>
    Console.WriteLine($"[{msg.Sender}]: {msg.Text}")
);

client.SendOnTcp("chat", new ChatMessage { Text = "Hello everyone!", Sender = "Player1" });
```

### 5.2 Game state sync (UDP vị trí)

```csharp
// === SERVER ===
server.SubscribeUdp<Position>("move", (conn, pos) =>
{
    // Gửi vị trí tới tất cả client KHÁC
    // Cần tự implement broadcast trừ sender: lặp qua Connections, bỏ qua conn.Id
    foreach (var other in server.Connections)
    {
        if (other.Id != conn.Id && other.UdpAddress != null)
            server.SendOnUdp("move", other, pos);
    }
});

// === CLIENT ===
var players = new Dictionary<string, Vector3>();

client.SubscribeUdp<Position>("move", pos =>
{
    // Cập nhật vị trí người chơi khác
    UpdateRemotePlayer(pos);
});

// Gửi liên tục (mỗi frame)
void Update()
{
    if (client.IsConnected)
        client.SendOnUdp("move", new Position { X = transform.x, Y = transform.y, Z = transform.z });
}
```

### 5.3 REST API cho dữ liệu game

```csharp
// === SERVER ===
server.OnGetRequest<PlayerStats>("get_stats", async user =>
{
    var stats = await db.GetPlayerStats(user.Id);
    return stats;  // PlayerStats : IMessage<PlayerStats>
});

server.OnPostRequest<BuyItemRequest, BuyItemResponse>("buy_item", async (user, req) =>
{
    var result = await shop.BuyItem(user.Id, req.ItemId, req.Quantity);
    return new BuyItemResponse { Success = result.Success, NewBalance = result.Balance };
});

// === CLIENT ===
var stats = await client.GetRequest<PlayerStats>("get_stats");
Console.WriteLine($"HP: {stats.Hp}, Level: {stats.Level}");

var buy = await client.PostRequest<BuyItemRequest, BuyItemResponse>("buy_item",
    new BuyItemRequest { ItemId = "sword_01", Quantity = 1, PlayerId = userId });
Console.WriteLine(buy.Success ? $"Đã mua! Số dư: {buy.NewBalance}" : "Thất bại");
```

### 5.4 Tự động reconnect khi mất mạng

```csharp
client.OnDisconnect(async () =>
{
    Console.WriteLine("Mất kết nối, đang thử lại...");
    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(2000);
        try
        {
            await client.ConnectServer();
            Console.WriteLine("Đã kết nối lại!");
            return;
        }
        catch (ConnectionFailedException)
        {
            Console.WriteLine($"Thử lần {i + 1} thất bại");
        }
    }
    Console.WriteLine("Không thể kết nối lại sau 5 lần thử");
});
```

### 5.5 Đăng nhập nhiều tài khoản (relogin)

```csharp
// Đăng nhập tài khoản 1
await client.Login(() => new LoginRequest { Username = "player1", Password = "pass1" });
await client.ConnectServer();

// ... chơi với player1 ...

// Đăng xuất player1
await client.Logout();

// Đăng nhập tài khoản 2
await client.Login(() => new LoginRequest { Username = "player2", Password = "pass2" });
await client.ConnectServer();
```

---

## 6. Mã cảnh báo

| Mã | Nghĩa | Xuất hiện khi |
|---|---|---|
| `W001` | UDP ping timeout | UDP không phản hồi ping trong `udpPingTimeoutMs` |
| `W002` | UDP handshake thất bại | Không nhận được key xác thực UDP sau 5 lần thử |
| `W003` | TCP chưa sẵn sàng | Gọi `SendOnTcp` khi chưa gọi `ConnectServer()` |
| `W004` | UDP thất bại | Gọi `SendOnUdp` nhưng UDP chưa sẵn sàng hoặc lỗi mạng |
| `W005` | WebSocket đã đóng | Server đóng WebSocket hoặc mạng mất |
| `W006` | Rớt tin TCP | Nhận được message TCP nhưng không có subscriber cho subject đó |
| `W007` | Rớt tin UDP | Nhận được message UDP nhưng không có subscriber cho subject đó |

---

## 7. Lưu ý quan trọng

1. **Gọi `Login()` trước `ConnectServer()`** — Token JWT từ login được dùng để xác thực WebSocket và REST API.

2. **`DisconnectAsync` khác `Logout`** — `DisconnectAsync` chỉ ngắt kết nối mạng, token vẫn giữ để reconnect. `Logout` xóa toàn bộ phiên.

3. **UDP là optional** — Để `udpServer = ""` trong `ClientConfig` nếu không cần. Client vẫn hoạt động bình thường qua TCP và REST.

4. **Server chỉ .NET 9.0** — Gọi `IServer.Create()` trên `netstandard2.1` sẽ ném `PlatformNotSupportedException`.

5. **WebGL không hỗ trợ** — `HttpClient` và `UdpClient` không hoạt động trong browser sandbox.

6. **Tất cả message dùng Protobuf** — Mọi tham số `TData`, `TRequest`, `TResponse` đều phải implement `IMessage<T>` (generated từ `.proto`).

7. **Luôn dùng `await using` hoặc gọi `DisposeAsync`** — Để cleanup WebSocket, UDP, HttpClient và SemaphoreSlim đúng cách.

8. **Token hết hạn tự động re-login** — Client gọi `Login<T>` lưu `_reLoginFactory`, khi REST request trả về `TokenExpired` sẽ tự re-login 1 lần rồi retry.
