# Plan: Thêm Logout — Tách biệt Ngắt kết nối và Xóa phiên

## Vấn đề

`DisconnectAsync()` trong `ClientImplement` hiện xóa `_token = null` và `_reLoginFactory = null`. Khi user gọi `ConnectServer()` lại sau `DisconnectAsync()`, `ConnectWebSocket` gửi token rỗng → server từ chối WebSocket upgrade (HTTP 401) → NativeWebSocket bắn `OnError("Unable to connect to the remote server")` → throw `ConnectionFailedException`.

**Nguyên nhân gốc**: `DisconnectAsync` nên chỉ đóng kết nối mạng (WS + UDP), không nên xóa trạng thái auth (`_token`, `_reLoginFactory`). Việc xóa token chỉ nên xảy ra khi user chủ động **logout** hoặc **dispose**.

## Mục tiêu

- Thêm `Logout()` vào `IClient` — xóa phiên đăng nhập + ngắt kết nối
- `DisconnectAsync()` chỉ đóng kết nối mạng, giữ nguyên token để reconnect
- `_token = null` chỉ xảy ra trong 2 nơi: `Logout()` và `DisposeAsync()` (qua `Logout()`)
- Reconnect sau disconnect không còn lỗi `ConnectionFailedException`

---

## Giai đoạn 1: Interface

### 1.1 `src/IClient.cs`

Thêm method sau `DisconnectAsync()`:

```csharp
Task Logout();
```

Vị trí: giữa `DisconnectAsync` (dòng 13) và `SendOnUdp` (dòng 14).

---

## Giai đoạn 2: Abstract

### 2.1 `src/ClientAbstract.cs`

Thêm abstract method sau `DisconnectAsync()`:

```csharp
public abstract Task Logout();
```

Vị trí: giữa `DisconnectAsync` (dòng 22) và `DisposeAsync` (dòng 24).

---

## Giai đoạn 3: Production Implementation (ClientImplement)

### 3.1 `src/impl/ClientImplement.cs` — Sửa `DisconnectAsync`

**Xóa 2 dòng** ở cuối method `DisconnectAsync()`:

```csharp
// XÓA:
_token = null;
_reLoginFactory = null;
```

**Giữ nguyên**: `_http?.Dispose(); _http = null;` — HttpClient không phụ thuộc auth, vẫn cleanup resource. Khi login lại `LoginImpl` tạo `_http ??= new HttpClient(...)`.

> **Lưu ý**: nếu user muốn gọi `GetRequest`/`PostRequest` sau khi disconnect WS (dùng lại `_http` cũ với token còn valid), thì cần giữ `_http`. Tuy nhiên hiện `_http` dùng `??=` lazy init nên dù có dispose ở `DisconnectAsync` thì lần dùng tiếp theo vẫn tạo lại được. Giữ nguyên logic dispose `_http` ở `DisconnectAsync` cho an toàn resource.

### 3.2 `src/impl/ClientImplement.cs` — Thêm `Logout`

Thêm method mới sau `DisconnectAsync()`, trước `DisposeAsync()`:

```csharp
public override async Task Logout()
{
    _token = null;
    _reLoginFactory = null;
    _loginDataFactory = null;
    await DisconnectAsync();
}
```

Logic:
1. Xóa token → các API call sau này sẽ không gửi kèm token cũ
2. Xóa `_reLoginFactory` → không còn khả năng auto re-login
3. Xóa `_loginDataFactory` → release tham chiếu data delegate
4. Gọi `DisconnectAsync()` → đóng WS + UDP + dispose HttpClient

### 3.3 `src/impl/ClientImplement.cs` — Sửa `DisposeAsync`

Sửa `DisposeAsync` để gọi `Logout()` thay vì `DisconnectAsync()`:

```csharp
// Cũ:
public override async ValueTask DisposeAsync()
{
    await DisconnectAsync();

    if (_udpClient != null)
        await _udpClient.DisposeAsync();

    _sendLock.Dispose();
}

// Mới:
public override async ValueTask DisposeAsync()
{
    await Logout();

    if (_udpClient != null)
        await _udpClient.DisposeAsync();

    _sendLock.Dispose();
}
```

---

## Giai đoạn 4: Test Implementation (TestClient)

### 4.1 `MyConnection.Tests/ConnectionTests.cs` — Thêm `Logout` cho `TestClient`

`TestClient` extend `ClientAbstract` trực tiếp → phải implement `Logout`.

Thêm method sau `DisconnectAsync()` (khoảng dòng 520):

```csharp
public override async Task Logout()
{
    _token = null;
    await DisconnectAsync();
}
```

> `TestClient` không có `_reLoginFactory` hay `_http`, chỉ cần clear `_token`.

---

## Giai đoạn 5: Build & Verify

| # | Lệnh | Mục tiêu |
|---|---|---|
| 5.1 | `dotnet build` | Build cả `net9.0` + `netstandard2.1`, 0 error |
| 5.2 | `dotnet test` | Tất cả test pass (A1-D4), đặc biệt C3 (reconnect) |
| 5.3 | `dotnet pack -o nupkgs` | Đóng gói NuGet |
| 5.4 | `dotnet publish ConsoleDemo -c Release --self-contained true -r win-x64 -o ConsoleDemo\publish` | Build ConsoleDemo exe |

---

## Bonus: Bug phụ phát hiện trong quá trình phân tích

### B1: `OnEmptyDispatch` handlers cộng dồn mỗi lần reconnect

**File**: `ClientImplement.cs:44-45` trong `ConnectWebSocket()`

```csharp
// HIỆN TẠI — gọi += mỗi lần ConnectWebSocket:
_tcpDispatcher.OnEmptyDispatch += sub => FireWarning("W006", ...);
_udpDispatcher.OnEmptyDispatch += sub => FireWarning("W007", ...);
```

Sau N lần reconnect → warning bắn N lần cho cùng 1 sự kiện.

**Fix**: Di chuyển 2 dòng này vào constructor `ClientImplement(ClientConfig)`.

### B2: `_connectTask` không được null sau `DisconnectAsync`

Sau `DisconnectAsync()`, `_connectTask` vẫn giữ reference tới Task cũ. Không gây lỗi nhưng giữ reference không cần thiết. Có thể set `_connectTask = null` trong `DisconnectAsync` để GC sớm hơn.

---

## Luồng lifecycle sau khi sửa

```
┌─────────────────────────────────────────────────────────┐
│                                                         │
│  Login(data) ──→ REST login, set _token + _reLoginFactory
│       │                                                 │
│  ConnectServer() ──→ WS/UDP connect (dùng _token)       │
│       │                                                 │
│  GetRequest / PostRequest / SendOnTcp / SendOnUdp       │
│       │                                                 │
│  DisconnectAsync() ──→ đóng WS + UDP, token còn nguyên  │
│       │                                                 │
│  ConnectServer() ──→ reconnect OK (token vẫn valid)     │
│       │                                                 │
│  Logout() ──→ xóa token + reLoginFactory + disconnect   │
│       │                                                 │
│  Login(data) ──→ login mới (nếu muốn)                   │
│       │                                                 │
│  DisposeAsync() ──→ = Logout() + cleanup UDP + lock     │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

## File bị ảnh hưởng

| File | Thay đổi |
|---|---|
| `src/IClient.cs` | +`Task Logout();` |
| `src/ClientAbstract.cs` | +`public abstract Task Logout();` |
| `src/impl/ClientImplement.cs` | `DisconnectAsync`: bỏ `_token=null` + `_reLoginFactory=null`; thêm `Logout()`; `DisposeAsync` gọi `Logout()` |
| `MyConnection.Tests/ConnectionTests.cs` | `TestClient`: +`override Logout()` |
| `plan/logout.md` | **MỚI** — file plan này |

## Ghi chú

- **Server không cần thay đổi**: server không có khái niệm "logout" vì JWT stateless. Token hết hạn tự động bị từ chối ở `HandleWebSocketUpgrade` và `HandleRestRequest`.
- **Không breaking change**: `IClient` thêm method mới, nhưng mọi implementation cần update. `IServer` không đổi.
- **Reconnect flow**: user có thể gọi `DisconnectAsync()` rồi `ConnectServer()` nhiều lần mà không cần login lại, miễn là token chưa hết hạn.
- **Token hết hạn khi reconnect**: nếu token hết hạn, `ConnectWebSocket` vẫn sẽ fail với `ConnectionFailedException`. Có thể thêm auto-re-login trong `ConnectWebSocket` ở PR sau (dùng `_reLoginFactory` đã được giữ lại).
