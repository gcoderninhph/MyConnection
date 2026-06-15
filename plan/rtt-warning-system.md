# Kế hoạch: Đo độ trễ RTT + Hệ thống cảnh báo OnWarning

## Tổng quan

| Tính năng | Mô tả |
|-----------|-------|
| **RTT** | `IClient.LatestRttMs` — tự động đo qua chu kỳ ping/pong UDP có sẵn |
| **OnWarning** | `ISubscribe OnWarning(Action<WarningInfo>)` trên `IClient`; `ISubscribe OnWarning(Action<ServerWarningInfo>)` trên `IServer` |

---

## Giai đoạn 1 — Class `WarningInfo` + `ServerWarningInfo` (FILE MỚI)

**`src/impl/WarningInfo.cs`**

```csharp
namespace MyConnection;

public class WarningInfo
{
    public string Code { get; }
    public string Message { get; }
    public Exception? Exception { get; }
    public WarningInfo(string code, string message, Exception? exception = null)
    {
        Code = code;
        Message = message;
        Exception = exception;
    }
}

public sealed class ServerWarningInfo : WarningInfo
{
    public IConnection? Connection { get; }
    public ServerWarningInfo(string code, string message, IConnection? connection = null, Exception? exception = null)
        : base(code, message, exception)
    {
        Connection = connection;
    }
}
```

> `WarningInfo` không sealed (mở cho kế thừa). `ServerWarningInfo` sealed, thêm `IConnection?` để callback server biết kết nối nào gây ra cảnh báo.

---

## Giai đoạn 2 — RTT: `MessageEnvelope` thêm field #4 `long Ticks`

| Khu vực | Thay đổi |
|---------|----------|
| Property | `public long Ticks { get; set; }` |
| Clone ctor | Sao chép `Ticks` |
| `MergeFrom` | `case 4: Ticks = input.ReadInt64(); break;` |
| `WriteTo` | Nếu `Ticks != 0`: `WriteRawTag(32)` + `WriteInt64(Ticks)` |
| `CalculateSize` | +1 + `CodedOutputStream.ComputeInt64Size(Ticks)` |
| `Equals`/`GetHashCode` | Bao gồm `Ticks` |

> Field #4 tương thích ngược — client cũ tự động bỏ qua field không biết.

---

## Giai đoạn 3 — RTT: Sửa `UdpPingService`

| Thay đổi | Chi tiết |
|----------|----------|
| Field mới `_lastPingSentTicks` | `long` — timestamp Unix ms khi gửi ping |
| `OnTick()` | Gán `envelope.Ticks = now`, lưu `_lastPingSentTicks` |
| `OnPongReceived(long sentTicks)` | Đổi signature (trước đây không tham số). Nếu `sentTicks > 0`: `LatestRttMs = now - sentTicks`. Vẫn cập nhật `_lastPongTime`. |
| `LatestRttMs` | `public long? LatestRttMs { get; private set; }` |

---

## Giai đoạn 4 — RTT: `UdpListener` phản hồi

Trong `HandleDatagram`, khi nhận `__ping__`:

```csharp
var pongEnvelope = new MessageEnvelope
{
    Subject = "__pong__",
    Ticks = envelope.Ticks  // echo ngược lại
};
```

Server chỉ việc echo lại giá trị `Ticks` client đã gửi.

---

## Giai đoạn 5 — RTT: `ClientImplement`

- Trong `HandleUdpMessage`, handler `__pong__`: truyền `envelope.Ticks` vào `_udpPing.OnPongReceived(envelope.Ticks)`
- Property mới: `public long? LatestRttMs => _udpPing?.LatestRttMs;`
- `ClientAbstract`: thêm `public abstract long? LatestRttMs { get; }`
- `TestClient`: override trả về `null` (UDP chưa được cài đặt)

---

## Giai đoạn 6 — `SubjectDispatcher` sự kiện không có subscriber

```csharp
public event Action<string>? OnEmptyDispatch;
```

Trong `Dispatch()`: khi `_subscribers.TryGetValue(subject, out ...)` trả về false → `OnEmptyDispatch?.Invoke(subject)`.

---

## Giai đoạn 7 — Interface: thêm `OnWarning` + `LatestRttMs`

**`IClient.cs`** (+2 thành viên):

```csharp
ISubscribe OnWarning(Action<WarningInfo> onWarning);
long? LatestRttMs { get; }
```

**`IServer.cs`** (+1 thành viên):

```csharp
ISubscribe OnWarning(Action<ServerWarningInfo> onWarning);
```

**`ClientAbstract.cs`** (+2 abstract):

```csharp
public abstract ISubscribe OnWarning(Action<WarningInfo> onWarning);
public abstract long? LatestRttMs { get; }
```

**`ServerAbstract.cs`** (+1 abstract):

```csharp
public abstract ISubscribe OnWarning(Action<ServerWarningInfo> onWarning);
```

---

## Giai đoạn 8 — Client: nối dây cảnh báo (`ClientImplement`)

### Hạ tầng callback

Cùng pattern với `_onDisconnectCallbacks`:

```csharp
private readonly List<Action<WarningInfo>> _onWarningCallbacks = new();

private void FireWarning(string code, string message, Exception? ex = null)
{
    var info = new WarningInfo(code, message, ex);
    Action<WarningInfo>[] snapshot;
    lock (_gate) { snapshot = _onWarningCallbacks.ToArray(); }
    foreach (var cb in snapshot) cb(info);
}
```

### Mã cảnh báo client

| Mã | Thông điệp | Điểm kích hoạt |
|----|-----------|----------------|
| **W001** | `"UDP ping timeout, kết nối có thể đã mất"` | `OnUdpPingTimeout` — sau khi re-auth |
| **W002** | `"UDP handshake thất bại sau 5 lần thử"` | `RunUdpHandshake` — sau vòng retry, trước `TrySetException` |
| **W003** | `"Gửi TCP thất bại, WebSocket chưa kết nối"` | `SendOnTcp` — nếu `_ws == null \|\| _ws.State != WebSocketState.Open` |
| **W004** | `"Gửi UDP thất bại"` | `SendOnUdp` — `SendAsync` ném lỗi hoặc `_udpClient` null |
| **W005** | `"WebSocket đóng bất ngờ (mã: {code})"` | `_ws.OnClose` — sau khi đã kết nối thành công |
| **W006** | `"Tin nhắn TCP bị rơi, không có subscriber cho subject '{subject}'"` | Hook `_tcpDispatcher.OnEmptyDispatch` |
| **W007** | `"Tin nhắn UDP bị rơi, không có subscriber cho subject '{subject}'"` | Hook `_udpDispatcher.OnEmptyDispatch` |

### Vị trí nối dây

```
SendOnTcp:
  if (_ws == null || _ws.State != Open)
    => FireWarning("W003", "...");

SendOnUdp:
  await _udpReadyTcs.Task;
  try { await _udpClient.SendAsync(...); }
  catch (Exception ex) { FireWarning("W004", "...", ex); }

OnUdpPingTimeout:
  // Sau khi re-auth:
  FireWarning("W001", "...");

RunUdpHandshake:
  // Sau vòng retry, trước TrySetException:
  FireWarning("W002", "...");

_ws.OnClose:
  if (connectTcs.Task.IsCompletedSuccessfully)
    FireWarning("W005", $"WebSocket đã đóng (mã: {code})");

Constructor / NotifyConnectUdp:
  _tcpDispatcher.OnEmptyDispatch += sub => FireWarning("W006", $"...{sub}");
  _udpDispatcher.OnEmptyDispatch += sub => FireWarning("W007", $"...{sub}");
```

---

## Giai đoạn 9 — Server: nối dây cảnh báo

### `ConnectionRegistry` — hạ tầng callback

```csharp
private readonly List<Action<ServerWarningInfo>> _onWarningCallbacks = new();

internal void FireWarning(string code, string message, IConnection? connection = null, Exception? ex = null)
{
    var info = new ServerWarningInfo(code, message, connection, ex);
    Action<ServerWarningInfo>[] snapshot;
    lock (_onWarningCallbacks) { snapshot = _onWarningCallbacks.ToArray(); }
    foreach (var cb in snapshot) cb(info);
}

public ISubscribe OnWarning(Action<ServerWarningInfo> callback)
{
    lock (_onWarningCallbacks) { _onWarningCallbacks.Add(callback); }
    return new UnsubscribeHandle(() => { lock (_onWarningCallbacks) { _onWarningCallbacks.Remove(callback); } });
}
```

### Mã cảnh báo server

| Mã | Connection? | Thông điệp | Điểm kích hoạt |
|----|-------------|-----------|----------------|
| **W001** | Có | `"Gửi UDP thất bại, không có endpoint cho kết nối {id}"` | `ServerImplement.SendOnUdp` — bắt `InvalidOperationException` |
| **W002** | Có | `"Gửi UDP đến {id} thất bại"` | `ServerImplement.SendOnUdp` — bắt `Exception` chung |
| **W003** | Có | `"Gửi TCP đến {id} thất bại"` | `ServerImplement.SendOnTcp` — bắt `Exception` |
| **W006** | Không | `"Tin nhắn TCP bị rơi, không có subscriber cho subject '{subject}'"` | `ConnectionRegistry.Route(fromUdp: false)` — không subscriber |
| **W007** | Không | `"Tin nhắn UDP bị rơi, không có subscriber cho subject '{subject}'"` | `ConnectionRegistry.Route(fromUdp: true)` — không subscriber |

### Nối dây trong `ServerImplement`

```csharp
public override ISubscribe OnWarning(Action<ServerWarningInfo> onWarning)
    => _registry.OnWarning(onWarning);

// SendOnUdp:
public override async void SendOnUdp<TData>(string subject, IConnection connection, TData data)
{
    try
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
        await _udpListener!.SendTo(connection.Id, envelope.ToByteArray());
    }
    catch (InvalidOperationException)
    {
        _registry.FireWarning("W001", $"Gửi UDP thất bại, không có endpoint cho kết nối {connection.Id}", connection);
    }
    catch (Exception ex)
    {
        _registry.FireWarning("W002", $"Gửi UDP đến {connection.Id} thất bại", connection, ex);
    }
}

// SendOnTcp:
public override async void SendOnTcp<TData>(string subject, IConnection connection, TData data)
{
    try
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
        await ((ConnectionImplement)connection).SendAsync(envelope.ToByteArray());
    }
    catch (Exception ex)
    {
        _registry.FireWarning("W003", $"Gửi TCP đến {connection.Id} thất bại", connection, ex);
    }
}
```

### Nối dây trong `ConnectionRegistry.Route`

```csharp
public void Route(string connectionId, string subject, byte[] payload, bool fromUdp)
{
    var subscribers = !fromUdp ? _tcpSubscribers : _udpSubscribers;
    if (!subscribers.TryGetValue(subject, out var list))
    {
        var transport = fromUdp ? "UDP" : "TCP";
        FireWarning($"W{(fromUdp ? "007" : "006")}",
            $"Tin nhắn {transport} bị rơi, không có subscriber cho subject '{subject}'");
        return;
    }
    // ... logic duyệt subscriber hiện có
}
```

---

## Giai đoạn 10 — Tests

| Test | Mục đích | Cách thực hiện |
|------|----------|----------------|
| **D1** | `OnWarning` kích hoạt `W003` khi gửi TCP lúc chưa kết nối | Tạo client → `OnWarning` → `SendOnTcp` → kiểm tra mã `W003` |
| **D2** | `OnWarning` kích hoạt `W006` khi gửi TCP đến subject không có subscriber | Kết nối → `SendOnTcp("no_sub", ...)` → kiểm tra mã `W006` kèm tên subject |
| **D3** | Server `OnWarning` kích hoạt khi gửi đến subject không có subscriber | Kết nối → server `SendOnTcp("no_sub", conn, ...)` → kiểm tra server `W006` |
| **D4** | `LatestRttMs` là `null` khi UDP bị tắt | Kiểm tra `client.LatestRttMs is null` |

---

## Danh sách file: 12 sửa + 1 mới

| # | File | Hành động |
|---|------|-----------|
| 1 | `src/impl/WarningInfo.cs` | **MỚI** |
| 2 | `src/impl/MessageEnvelope.cs` | Thêm field #4 `Ticks` |
| 3 | `src/impl/UdpPingService.cs` | Theo dõi RTT + `LatestRttMs` |
| 4 | `src/impl/UdpListener.cs` | Echo `Ticks` trong pong |
| 5 | `src/impl/SubjectDispatcher.cs` | Thêm sự kiện `OnEmptyDispatch` |
| 6 | `src/impl/ClientImplement.cs` | Callback cảnh báo + `LatestRttMs` |
| 7 | `src/impl/ConnectionRegistry.cs` | Callback cảnh báo + cảnh báo route |
| 8 | `src/impl/ServerImplement.cs` | Cảnh báo khi gửi + lộ `OnWarning` |
| 9 | `src/IClient.cs` | +`OnWarning` + `LatestRttMs` |
| 10 | `src/IServer.cs` | +`OnWarning` |
| 11 | `src/ClientAbstract.cs` | +2 abstract |
| 12 | `src/ServerAbstract.cs` | +1 abstract |
| 13 | `MyConnection.Tests/ConnectionTests.cs` | +4 tests + `TestClient` overrides |
