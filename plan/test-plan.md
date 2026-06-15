# Kế hoạch test — MyConnection

> **Cập nhật:** 2025-06-15 — 1 file test duy nhất. Chỉ test 3 chức năng: Kết nối, gửi tin nhắn, ngắt kết nối.

---

## Cấu trúc

```
MyConnection.Tests/
├── MyConnection.Tests.csproj    (xUnit + FluentAssertions + NSubstitute)
└── ConnectionTests.cs           (file duy nhất)
```

**Framework:** xUnit + FluentAssertions + NSubstitute  
**Target:** net9.0  
**Tham chiếu:** project reference tới `MyConnection.csproj`

---

## Test Cases — `ConnectionTests.cs`

### Nhóm A: Kết nối (Connect)

| # | Test | Setup | Steps | Expected |
|---|------|-------|-------|----------|
| A1 | Client kết nối với token hợp lệ | Start server (port 0), tạo token, tạo `ClientConfig` | `client.ConnectServer(config)` | Server `OnConnect` fire, `IConnection != null` |
| A2 | Client kết nối với token sai | Start server, dùng token `"invalid.token.here"` | `client.ConnectServer(badConfig)` | Server `OnConnect` không fire |
| A3 | Client kết nối với token hết hạn | Start server, tạo token với expiry quá khứ | `client.ConnectServer(config)` | Server `OnConnect` không fire |

### Nhóm B: Gửi tin nhắn (Message)

| # | Test | Setup | Steps | Expected |
|---|------|-------|-------|----------|
| B1 | Client gửi tin → Server nhận | Client connected, server subscribe `"chat"` | `client.SendOnTcp("chat", msg)` | Server callback nhận đúng `msg` |
| B2 | Server gửi tin → Client nhận | Client connected, client subscribe `"echo"`, bắt `IConnection` qua `OnConnect` | `server.SendOnTcp("echo", conn, msg)` | Client callback nhận đúng `msg` |
| B3 | Hai client chat qua server | 2 clients connect, server subscribe `client2.Id` vào `"room"`, client2 subscribe `"room"` | `client1.SendOnTcp("room", msg)` | client2 nhận `msg`, client1 không nhận lại chính mình |

### Nhóm C: Ngắt kết nối (Disconnect)

| # | Test | Setup | Steps | Expected |
|---|------|-------|-------|----------|
| C1 | Server ngắt → Client `OnDisconnect` fire | Client connected, client `OnDisconnect` subscribe | `server.DisposeAsync()` | Client `OnDisconnect` callback fire |
| C2 | Client ngắt → Server `OnDisconnect` fire | Client connected, server `OnDisconnect` subscribe | Đóng WebSocket phía client | Server callback nhận đúng `IConnection` |
| C3 | Reconnect: subscription vẫn hoạt động sau khi kết nối lại | Client connect → disconnect → reconnect | Subscribe `"echo"`, server gửi msg | Client vẫn nhận được msg sau reconnect |

---

## Ghi chú triển khai

1. **Server startup:** dùng `ServerImplement.Create(config)` với `websocketEndpoint = "127.0.0.1:0/ws"` (port ngẫu nhiên), cần đọc port thực từ `TcpListener.LocalEndpoint`.

2. **Client WebSocket:** `Colyseus.NativeWebSocket` (Unity runtime) không chạy được trong .NET test host. Giải pháp:
   - Tạo `ClientTestImplement : ClientAbstract` override `ConnectWebSocket` dùng `System.Net.WebSockets.ClientWebSocket` thay vì NativeWebSocket.
   - Hoặc mock `ClientAbstract.ConnectWebSocket`.

3. **Chờ async:** Dùng `TaskCompletionSource<T>` + `WaitAsync(TimeSpan)` thay vì `Task.Delay` để tránh timing flaky.

4. **Cleanup:** Mỗi test tự cleanup server (`DisposeAsync`) và client WebSocket. Dùng `IAsyncLifetime` để setup/teardown.

5. **Port:** Server cần expose port thực sau khi bind. Có thể thêm internal property hoặc dùng `GetFreePort()` helper tìm port trước rồi truyền vào config.

---

## Tổng: 9 test cases, 1 file
