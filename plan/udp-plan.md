# UDP Communication Plan

## Overview

Thêm UDP transport vào MyConnection. WebSocket là control channel (auth, session management, reliable messages). UDP là best-effort data channel cho real-time data (position, input, state sync). Xác thực UDP dựa trên kết nối WebSocket đã có.

---

## Authentication Model

```
  Client                              Server
  ──────                              ──────

  1. ConnectWebSocket() ✅ (đã có)
     └─ JWT auth, IConnection tồn tại

  2. NotifyConnectUdp() [cần implement]
     ├─ Client gửi WS message "request_udp_auth"
     ├─ Server gen sessionKey (8 bytes random)
     ├─ Server lưu sessionKey → connectionId vào UdpSessionMap
     ├─ Server reply WS: MessageEnvelope { Subject="__udp_auth__", Payload=sessionKey }
     ├─ Client nhận sessionKey → tạo UdpClient
     └─ Client gửi UDP datagram chứa sessionKey:
         MessageEnvelope { Subject="__ping__", SessionKey=sessionKey }
         │
         └─ Nếu gói bị mất → RETRY LOOP (xem section "UDP Handshake Retry")

  3. Khi server nhận datagram chứa sessionKey hợp lệ:
     ├─ Bind (IP, port) → connectionId vào UdpSessionMap
     ├─ Gửi WS ACK "udp_bound" về client (idempotent)
     └─ Từ đây, mọi datagram từ cùng (IP, port) → lookup thẳng

  4. Tất cả UDP datagram sau:
     Không cần sessionKey — server đã bind (IP, port) → IConnection
```

### Tại sao bind IP:port mà không gửi key mỗi packet?

- **Performance**: Zero overhead per-packet sau handshake
- **Đủ an toàn**: Client và server đều là trusted code (game engine), sessionKey chỉ để ngăn spoofing lúc đầu
- **NAT-friendly**: UDP hole punching tự nhiên từ client → server

---

## UDP Handshake Retry (Lost Packet Recovery)

UDP datagram chứa sessionKey có thể bị mất do bản chất connectionless của UDP.
Cơ chế: **client retry qua UDP + server ACK qua WS.**

```
Client                                        Server
──────                                        ──────

  Nhận sessionKey từ WS reply
  │
  ├─ Tạo UdpClient, gửi datagram #1
  │   { SessionKey, Subject="__ping__" } ──lost──→  (không nhận)
  │
  ├─ Timer 500ms, không thấy WS ACK
  │
  ├─ Gửi datagram #2
  │   { SessionKey, Subject="__ping__" } ──lost──→  (không nhận)
  │
  ├─ Timer 500ms, không thấy WS ACK
  │
  ├─ Gửi datagram #3
  │   { SessionKey, Subject="__ping__" } ──ok───→  Nhận, SessionKey khớp
  │                                                   ├─ Bind (IP,port) → connectionId
  │                                                   ├─ Gửi WS ACK "udp_bound"
  │                                                   └─ Route "__ping__" như bình thường
  │
  │  ←── WS "udp_bound" ───────────────────────
  │
  ├─ ACK nhận được → dừng retry, đánh dấu UDP ACTIVE
  │
  └─ Ngừng embed SessionKey, _udpReadyTcs.SetResult()

  ════════════════ UDP ACTIVE ════════════════
```

### Retry Parameters

| Tham số | Giá trị | Giải thích |
|---------|---------|-----------|
| Retry interval | 500ms | Đủ ngắn để handshake nhanh, đủ dài để không spam |
| Max retries | 5 | Tổng timeout = 5 × 500ms = 2.5s |
| Timeout action | `_udpReadyTcs.SetException(new ConnectionFailedException(...))` | `SendOnUdp` sẽ throw nếu gọi sau đó |

### Server Idempotency

Server phải xử lý duplicate datagram handshake an toàn:

```
UdpListener.ReceiveLoop nhận datagram có SessionKey:
  ├─ if _sessionMap.IsAlreadyBound(sessionKey):
  │     → Endpoint đã bind, gửi lại WS ACK "udp_bound"  (idempotent)
  │     → return
  └─ else:
        → BindEndpoint(remoteEP, sessionKey)
        → Gửi WS ACK "udp_bound"
        → Route "__ping__" nếu có
```

### Client State Machine

```
  IDLE ──(WS reply sessionKey)──→ HANDSHAKING
                                      │
                            ┌─ retry timer 500ms ─┐
                            │    gửi datagram      │
                            │    retryCount++       │
                            └───────────────────────┘
                                      │
                      ┌───────────────┼───────────────┐
                      │               │               │
                 WS ACK nhận     retryCount > max    (loop tiếp)
                      │               │
                 UDP ACTIVE    SetException          HANDSHAKING
                               (timeout)            (giữ nguyên)
```

### Edge Cases

| Case | Server behavior | Client behavior |
|------|----------------|-----------------|
| Datagram đến sau khi đã bind (duplicate) | Gửi lại WS ACK, không đổi bind | Nhận ACK → dừng retry |
| Datagram đến với key sai / không có key | Drop (ignore) | Không ảnh hưởng |
| Client retry hết 5 lần | Không biết (server chưa nhận gói nào) | `_udpReadyTcs.SetException` |
| WS đứt giữa lúc retry | Cleanup toàn bộ | `_cts.Cancel()` → dừng retry |

---

## Wire Format

Extend `MessageEnvelope` proto (hand-rolled, không `.proto` file):

```
Field 1: string Subject
Field 2: bytes  Payload
Field 3: bytes  SessionKey   ← NEW (WireType.LengthDelimited, field number 3)
```

- **TCP**: SessionKey luôn để trống (0 byte) → zero overhead
- **UDP**: Datagram đầu tiên có SessionKey, các datagram sau không cần

### MessageEnvelope.cs changes

- Add property `ByteString SessionKey` (field #3)
- Update `CalculateSize()`, `WriteTo()`, `MergeFrom()` để xử lý field 3
- Update `Clone()`, `Equals()`, `GetHashCode()`
- Giữ nguyên `Parser` static instance

---

## UDP Session Lifecycle

```
                    ConnectServer(config)
                         │
                    ┌─ ConnectWebSocket (JWT auth) ─┐
                    │  → IConnection tồn tại         │
                    └────────────────────────────────┘
                         │
               NotifyConnectUdp() (fire-and-forget)
                         │
         ┌───────────────┴───────────────┐
         │  1. WS request "__udp_auth"   │
         │  2. Server gen sessionKey     │
         │  3. WS reply sessionKey       │
         │  4. Tạo UdpClient             │
         │  5. Gửi datagram có SessionKey│
         │  6. Server bind (IP,port)→conn│
         │  7. Start UDP recv loop       │
         └───────────────────────────────┘
                         │
               ╔══════════════════╗
               ║ UDP ACTIVE       ║  SendOnUdp, SubscribeUdp hoạt động
               ║ Ping/pong định kỳ║  Client → ping, Server → pong (qua UDP)
               ╚══════╤═══════════╝
                      │
          ┌───────────┼───────────┐
          │           │           │
    Pong timeout  DisconnectAsync  WS đứt
     (default 15s)     │           │
          │             │           │
     ┌ Re-auth ──┐      │           │
     │ 1. Reset   │      │           │
     │ 2. WS req  │      │           │
     │    key mới │      │           │
     │ 3. Retry   │      │           │
     │    handshake     │           │
     │ 4. Bind mới│      │           │
     └────────────┘      │           │
          │             │           │
     Thành công:   Close Udp    Close Udp
     UDP ACTIVE    Close WS     Fire callbacks
          │             │           │
          └─────────────┴───────────┘
                      │
               Server cleanup
                      │
               ╔══════════════════╗
               ║ UDP DEAD         ║
              ╚══════════════════╝
```

| Phase | Trigger | Hành động |
|-------|---------|-----------|
| **Start** | WebSocket connected, `NotifyConnectUdp()` | Handshake + bind |
| **Active** | Running | SendOnUdp, SubscribeUdp, ping/pong theo interval config |
| **Pong timeout** | `elapsed > udpPingTimeoutMs` | Reset `_udpReadyTcs`, re-auth qua WS, retry handshake, bind mới |
| **Re-auth thành công** | WS ACK + bind mới | UDP ACTIVE trở lại, `SendOnUdp` resume |
| **Re-auth thất bại** | WS re-auth retry hết | `_udpReadyTcs.SetException` |
| **End (chủ động)** | `DisconnectAsync()` | Đóng cả 2 transport |
| **End (bị động)** | WebSocket OnClose | Cleanup cả 2 transport |
| **Terminal** | `DisposeAsync()` | Disconnect + dispose _sendLock, không reconnect được |

**Pong timeout không giết WebSocket.** WS là control channel sống độc lập. Khi UDP timeout, client dùng WS để re-auth: request key mới → server gen key mới, xoá key cũ → client retry handshake UDP → bind endpoint mới (có thể khác port nếu NAT thay đổi).

**Pong nhận qua UDP, không qua WS.** Ping/pong cùng kênh mới meaningful — nếu pong qua WS thì không chứng minh được đường UDP còn sống.

`ConnectServer()` không block chờ UDP ready — return ngay sau WS handshake. `SendOnUdp` nội bộ await `_udpReadyTcs` nếu UDP chưa sẵn sàng.

---

## New Files

### 1. `src/impl/UdpSessionMap.cs`

Server-side. Quản lý mapping giữa session key, endpoint, và connection.

```
ConcurrentDictionary<string, string>  _keyToConnection    // sessionKey hex → connectionId
ConcurrentDictionary<string, IPEndPoint> _connectionToEndpoint  // connectionId → remote EP
ConcurrentDictionary<IPEndPoint, string> _endpointToConnection  // remote EP → connectionId
```

Methods:
- `RegisterKey(string key, string connectionId)` — lưu key → connection
- `BindEndpoint(IPEndPoint ep, string key)` — ghi nhận endpoint cho key, populate cả 2 chiều
- `bool IsAlreadyBound(string key)` — true nếu key đã có endpoint binding
- `GetConnectionId(IPEndPoint ep)` — tra cứu nhanh
- `GetEndpoint(string connectionId)` — tra cứu ngược
- `Invalidate(string connectionId)` — xoá endpoint binding nhưng giữ key (cho re-auth)
- `Remove(string connectionId)` — cleanup hoàn toàn khi disconnect (xoá key + endpoint)
- `string GenerateKey()` — `Guid.NewGuid().ToString("N").Substring(0, 16)` (8 bytes hex)

### 2. `src/impl/UdpListener.cs`

Server-side. `#if NET9_0` only.

```
class UdpListener
{
    UdpClient _udpClient;
    UdpSessionMap _sessionMap;
    ConnectionRegistry _registry;
    CancellationTokenSource _cts;

    Task StartAsync(int port, CancellationToken ct)
        → bind UdpClient to port
        → fire-and-forget ReceiveLoop

    async Task ReceiveLoop()
        while not cancelled:
            result = await _udpClient.ReceiveAsync()
            → parse MessageEnvelope
            → if subject == "__ping__":
                → Gửi pong ngay: MessageEnvelope { Subject="__pong__" }
                → _udpClient.SendAsync(pongBytes, remoteEP)  // fire-and-forget
                → if SessionKey not empty → xử lý như handshake bên dưới
                → else → update UdpPingTime, return
            → if SessionKey not empty:
                ├─ if _sessionMap.IsAlreadyBound(SessionKey):
                │     → Gửi WS ACK "udp_bound" về client (idempotent retry)
                │     → Route(subject, payload) như bình thường
                │     → return
                └─ else:
                      → BindEndpoint(remoteEP, SessionKey)
                      → Gửi WS ACK "udp_bound" về client
                      → Route(subject, payload)
            → else:
                → lookup _sessionMap.GetConnectionId(remoteEP)
                → if found:
                    → update conn.UdpPingTime = now()
                    → _registry.Route(connectionId, subject, payload)
                → else → drop

    async Task SendTo(string connectionId, byte[] data)
        → lookup _sessionMap.GetEndpoint(connectionId)
        → if found → _udpClient.SendAsync(data, endpoint)
        → else → throw (no UDP bound)
}
```

### 3. `src/impl/UdpClientWrapper.cs`

Client-side. Multi-target: `#if NET9_0` dùng async, netstandard2.1 dùng sync fallback.

```
class UdpClientWrapper : IAsyncDisposable
{
    UdpClient _udpClient;
    IPEndPoint _serverEP;
    CancellationTokenSource _cts;

    Task ConnectAsync(string serverEndpoint)
        → Parse "ip:port"
        → new UdpClient()
        → _udpClient.Connect(parsedEP)
        → Start receive loop (fire-and-forget)

    Task SendAsync(byte[] data)
        → await _udpClient.SendAsync(data, data.Length)

    event Action<byte[]> OnMessage

    async Task ReceiveLoop()
        while not cancelled:
            result = await _udpClient.ReceiveAsync()
            → OnMessage?.Invoke(result.Buffer)

    async Task CloseAsync()
        → _cts.Cancel()
        → _udpClient.Close()
}
```

Unity (netstandard2.1): `System.Net.Sockets.UdpClient` hoạt động trên Windows/Mac/iOS/Android/Linux. **WebGL không hỗ trợ** — `NotImplementedException`.

### 4. `src/impl/UdpHandshakeHandler.cs`

Server-side logic cho UDP handshake flow (WS side).

```
class UdpHandshakeHandler
{
    UdpSessionMap _sessionMap;
    ConnectionRegistry _registry;

    void OnUdpAuthRequest(IConnection connection, byte[] payload)
        → key = _sessionMap.GenerateKey()
        → _sessionMap.Invalidate(connection.Id)   // Xoá endpoint cũ nếu có
        → _sessionMap.RegisterKey(key, connection.Id)  // Key mới
        → Gửi WS reply cho connection:
            new MessageEnvelope { Subject="__udp_auth__", Payload=key }
```

### 5. `src/impl/UdpPingService.cs`

Client-side. Background timer gửi ping qua UDP, theo dõi pong, phát hiện timeout.

```
class UdpPingService : IDisposable
{
    UdpClientWrapper _udpClient;
    Timer _timer;
    int _intervalMs;
    int _timeoutMs;
    long _lastPongTime;
    CancellationTokenSource _cts;

    UdpPingService(UdpClientWrapper udpClient, int intervalMs, int timeoutMs)
        → _udpClient = udpClient
        → _intervalMs = intervalMs
        → _timeoutMs = timeoutMs

    void Start()
        → _lastPongTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        → _timer = new Timer(OnTick, null, 0, _intervalMs)

    void OnTick(object _)
        long now    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        long elapsed = now - _lastPongTime

        // 1. Gửi ping
        envelope = new MessageEnvelope { Subject = "__ping__" }
        _ = _udpClient.SendAsync(envelope.ToByteArray())

        // 2. Kiểm tra timeout
        if (elapsed > _timeoutMs)
            OnPingTimeout?.Invoke()  // → ClientImplement re-auth

    void OnPongReceived()
        → _lastPongTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

    event Action OnPingTimeout  // Fire khi timeout

    void Dispose()
        → _timer?.Dispose()
        → _cts?.Cancel()
}
```

**Pong nhận qua UDP, không qua WS.** Ping/pong cùng kênh mới chứng minh được đường UDP còn sống.

### 6. ~~`src/impl/UdpAuthReply.cs`~~ — Không cần

SessionKey được gửi dưới dạng raw bytes trong `MessageEnvelope.Payload` (subject `"__udp_auth__"` và `"__udp_bound__"`). Không cần proto message riêng.

---

## Configuration Changes

### `src/impl/ClientConfig.cs`

```
public class ClientConfig
{
    public string token;
    public string websocketServer;
    public string udpServer;

    // NEW — UDP ping/pong (chỉ dùng khi udpServer != null)
    public int udpPingIntervalMs = 5000;   // Khoảng cách giữa 2 ping
    public int udpPingTimeoutMs  = 15000;  // Tổng thời gian không pong → re-auth
}
```

Người dùng FPS có thể set `udpPingIntervalMs=1000, udpPingTimeoutMs=3000`.

### `src/impl/ServerConfig.cs`

Không cần thay đổi — ping interval và timeout là client-side config. Server chỉ reply pong, không quản lý timeout.

---

## Modified Files

### `src/impl/MessageEnvelope.cs`

Add field 3 `ByteString SessionKey`:

- WireType.LengthDelimited, field number 3
- Default: `ByteString.Empty` (TCP will never set it)
- Update: `CalculateSize()`, `WriteTo()`, `MergeFrom()`, `Clone()`, `Equals()`, `GetHashCode()`

### `src/impl/ClientImplement.cs`

Add fields:
- `UdpClientWrapper _udpClient`
- `UdpPingService _udpPing`
- `TaskCompletionSource _udpReadyTcs` — `SendOnUdp` await cái này nếu UDP chưa ready
- `ISubscribe _pongSubscription` — subscription `"__pong__"` để nhận pong từ server
- `ClientConfig _config` — lưu lại config để re-auth dùng lại

Implement `NotifyConnectUdp(string token, string udpServer)`:
```
1. if udpServer is null/empty → return (UDP disabled)
2. _udpClient = new UdpClientWrapper()
3. _udpClient.OnMessage += HandleUdpMessage
4. await _udpClient.ConnectAsync(udpServer)
5. Subscribe WS "__pong__" → _udpPing.OnPongReceived()
6. Gửi WS message "request_udp_auth" → chờ reply sessionKey
7. Subscribe WS "__udp_bound__" → khi nhận → dừng retry, _udpReadyTcs.SetResult()
8. RETRY LOOP:
   a. retryCount = 0
   b. while retryCount < 5:
        - envelope = new MessageEnvelope { Subject="__ping__", SessionKey=key }
        - await _udpClient.SendAsync(envelope.ToByteArray())
        - await Task.Delay(500)
        - if _udpReadyTcs.Task.IsCompleted → break (đã bound)
        - retryCount++
   c. if still not bound → _udpReadyTcs.SetException(new ConnectionFailedException(...))
9. _udpPing = new UdpPingService(_udpClient, config.udpPingIntervalMs, config.udpPingTimeoutMs)
10. _udpPing.OnPingTimeout += OnUdpPingTimeout  // re-auth handler
11. _udpPing.Start()
```

Implement `OnUdpPingTimeout()` — re-auth flow:
```
1. _udpPendingTcs = new TaskCompletionSource()  // Reset ready state
2. _udpPing?.Dispose()                          // Dừng ping cũ
3. Gửi WS "request_udp_auth" → chờ key mới
4. RETRY LOOP (giống bước 8 ở trên, key mới)
5. _udpPing = new UdpPingService(...)           // Ping mới
6. _udpPing.OnPingTimeout += OnUdpPingTimeout
7. _udpPing.Start()
```

Implement `HandleUdpMessage(byte[] data)`:
```
→ MessageEnvelope.Parser.ParseFrom(data)
→ if subject == "__pong__":
    → _udpPing?.OnPongReceived()
    → return  // Không dispatch pong lên app layer
→ _dispatcher.Dispatch(subject, payload)
```

Implement `AutoPingWebSocketAndUdpThread()`:
```
→ _udpPing?.Start() nếu chưa start
```

Implement `SendOnUdp<TData>(subject, data)`:
```
1. await _udpReadyTcs.Task (hoặc throw nếu UDP disabled)
2. Serialize data
3. envelope = new MessageEnvelope { Subject=subject, Payload=bytes }
4. await _udpClient.SendAsync(envelope.ToByteArray())
```

Implement `SubscribeUdp<TData>(subject, callback)`:
```
→ return _dispatcher.Subscribe(subject, callback)  // chung dispatcher với TCP
```

Implement `HandleUdpMessage(byte[] data)`:
```
→ MessageEnvelope.Parser.ParseFrom(data)
→ _dispatcher.Dispatch(subject, payload)
```

Update `DisconnectAsync()`:
```
1. _udpPing?.Dispose()
2. if _udpClient != null → await _udpClient.CloseAsync()
3. (existing WS close logic)
```

Update `DisposeAsync()`:
```
→ await DisconnectAsync()
→ _udpClient?.Dispose()
→ _sendLock.Dispose()
```

Add `_udpReadyTcs.Reset()` trước mỗi `ConnectWebSocket` để hỗ trợ reconnect.

### `src/impl/ServerImplement.cs`

Add fields:
- `UdpListener _udpListener`
- `UdpSessionMap _sessionMap`
- `UdpHandshakeHandler _handshakeHandler`

Constructor changes:
```
→ _sessionMap = new UdpSessionMap()
→ _handshakeHandler = new UdpHandshakeHandler(_sessionMap)
→ Đăng ký SubscribeTcp("request_udp_auth", _handshakeHandler.OnUdpAuthRequest)
→ _udpListener = new UdpListener(_sessionMap, _registry)
```

`Create(ServerConfig config)`:
```
→ _ = _udpListener.StartAsync(config.udpPort, _cts.Token)
```

Implement `SendOnUdp<TData>(subject, conn, data)`:
```
1. Serialize data
2. envelope = new MessageEnvelope { Subject=subject, Payload=bytes }
3. await _udpListener.SendTo(conn.Id, envelope.ToByteArray())
```

Implement `SendAllOnUdp<TData>(subject, data)`:
```
1. Serialize data
2. envelope = new MessageEnvelope { Subject=subject, Payload=bytes }
3. bytes = envelope.ToByteArray()
4. foreach conn in _registry.GetAll() where conn.UdpAddress != ""
5.     _ = _udpListener.SendTo(conn.Id, bytes)  // fire-and-forget
```

Implement `SubscribeUdp<TData>(subject, callback)`:
```
→ return _registry.SubscribeLocal<TData>(subject, callback)  // chung dispatcher
```

Update `DisposeAsync()`:
```
→ _udpListener?.StopAsync()
→ (existing cleanup)
```

### `src/impl/ConnectionImplement.cs`

`UdpAddress` — set từ `UdpListener` sau khi bind endpoint thành công:
```
public string UdpAddress { get; set; }  // "192.168.1.5:54321"
```

`UdpPingTime` — update mỗi khi nhận UDP message từ connection này:
```
public long UdpPingTime { get; set; }  // DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
```

### `src/impl/ConnectionRegistry.cs`

Add method:
```
void BindUdp(string connectionId, string endpoint)
    → connection = _connections[connectionId]
    → connection.UdpAddress = endpoint
```

Add cleanup trong `Remove()`:
```
→ _sessionMap.Remove(connectionId)  // xoá session key + endpoint binding
```

---

## Test Plan

All tests go in `MyConnection.Tests/ConnectionTests.cs` (hoặc file riêng `UdpConnectionTests.cs`).

### UDP-Specific Tests

| ID | Name | Description |
|----|------|-------------|
| D1 | **UDP handshake thành công** | Client connect WS → UDP handshake → server `_sessionMap` có key → `UdpAddress` != "" |
| D2 | **UDP disabled (udpServer null)** | Client config.udpServer = null → `SendOnUdp` throw `InvalidOperationException` |
| D3 | **SendOnUdp client→server** | Send "hello" qua UDP → server `SubscribeTcp` nhận được (vì chung dispatcher) |
| D4 | **SendOnUdp server→client** | Server gửi "world" qua UDP tới connection → client `SubscribeUdp` nhận được |
| D5 | **SendAllOnUdp** | 2 clients có UDP → server `SendAllOnUdp` → cả 2 nhận, client không UDP thì không nhận |
| D6 | **Subscribe chung TCP+UDP** | Subscribe 1 subject → gửi 1 msg TCP + 1 msg UDP → callback fire 2 lần |
| D7 | **UDP disconnect cleanup** | Client disconnect → server `_sessionMap` không còn key → `UdpAddress` = "" |
| D8 | **Server shutdown UDP cleanup** | Server `DisposeAsync` → `UdpListener` stop → client `OnDisconnect` fire |
| D9 | **UDP reconnect** | Disconnect → ConnectServer lại → UDP handshake mới → sessionKey mới → bind mới |
| D10 | **UDP before ready** | `SendOnUdp` gọi ngay sau `ConnectServer` (trước khi handshake xong) → **không throw**, await nội bộ đến khi ready |
| D11 | **UDP retry on lost datagram** | Server tắt UdpListener → client gửi handshake datagram (mất) → retry × 3 → server bật UdpListener → datagram thứ 4 đến → bind + ACK → UDP active |
| D12 | **UDP ping/pong** | Client gửi ping → server reply pong qua UDP → client `_lastPongTime` update |
| D13 | **UDP pong timeout → re-auth** | Client config timeout=1s, interval=500ms → block server pong trong 1.5s → client detect timeout → re-auth qua WS → key mới → bind mới → UDP ACTIVE |
| D14 | **UDP pong timeout → thất bại** | Client re-auth nhưng server không phản hồi → retry hết → `_udpReadyTcs.SetException` |
| D15 | **Pong qua UDP, không qua WS** | Verify pong reply được gửi qua UDP (không qua WS) |
| D16 | **Re-auth giữa chừng, SendOnUdp vẫn await** | Đang active → pong timeout → `SendOnUdp` gọi lúc re-auth → await đến khi re-auth xong → gửi thành công |
| D17 | **Configurable ping interval** | Test với `udpPingIntervalMs=200` → verify ping gửi mỗi ~200ms |

---

## File Tree After Implementation

```
src/
├── IClient.cs              (có sẵn, không đổi)
├── IServer.cs              (có sẵn, không đổi)
├── IConnection.cs          (có sẵn, không đổi)
├── ISubscribe.cs           (có sẵn, không đổi)
├── IUser.cs                (có sẵn, không đổi)
├── ClientAbstract.cs       (có sẵn, không đổi)
├── ServerAbstract.cs       (có sẵn, không đổi)
├── ConnectionFailedException.cs
└── impl/
    ├── ClientImplement.cs          ← SỬA: UDP fields, implementations
    ├── ServerImplement.cs          ← SỬA: UDP fields, implementations
    ├── ConnectionImplement.cs      ← SỬA: UdpAddress, UdpPingTime
    ├── ConnectionRegistry.cs       ← SỬA: BindUdp, cleanup
    ├── ClientConfig.cs             ← SỬA: +udpPingIntervalMs, udpPingTimeoutMs
    ├── ServerConfig.cs             (có sẵn, không đổi)
    ├── ServerTokenService.cs       (có sẵn, không đổi)
    ├── WebSocketListener.cs        (có sẵn, không đổi)
    ├── SubjectDispatcher.cs        (có sẵn, không đổi)
    ├── MessageEnvelope.cs          ← SỬA: field 3 SessionKey
    ├── ProtoSerializer.cs          (có sẵn, không đổi)
    ├── UdpSessionMap.cs            ← MỚI
    ├── UdpListener.cs              ← MỚI (#if NET9_0)
    ├── UdpClientWrapper.cs         ← MỚI
    ├── UdpHandshakeHandler.cs      ← MỚI (#if NET9_0)
    └── UdpPingService.cs           ← MỚI
```

---

## Constraints

- **Multi-target**: `net9.0;netstandard2.1`, `LangVersion 10`
- `#if NET9_0` cho server code (UdpListener, UdpHandshakeHandler, ServerImplement UDP methods)
- **Unity (netstandard2.1)**: `System.Net.Sockets.UdpClient` hoạt động trên hầu hết platform; WebGL → `NotImplementedException`
- **0 NuGet cho netstandard2.1**: DLL bundle giống như hiện tại (dùng thẳng BCL)
- `IAsyncDisposable` cho `UdpClientWrapper` cleanup
- Subscription **chung SubjectDispatcher** cho cả TCP và UDP

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| WebGL không hỗ trợ UDP | CRITICAL | Document, throw NotImplementedException rõ ràng |
| NAT/router chặn UDP | MEDIUM | AutoPing giữ NAT hole; document fallback TCP |
| UDP before ready gây race | MEDIUM | `_udpReadyTcs` await nội bộ, caller không cần biết |
| MessageEnvelope field 3 tương thích ngược | LOW | Field 3 optional, TCP không set → zero byte overhead |
| Reconnect với UDP stale state | MEDIUM | Reset `_udpReadyTcs` trước connect; UdpSessionMap cleanup khi disconnect |
| UDP handshake datagram bị mất | HIGH | Retry loop 500ms × 5 + WS ACK; server idempotent bind |
| UDP ping/pong mất gói gây false re-auth | MEDIUM | Timeout = 3 × interval (default 15s); không re-auth vì 1 gói mất |
| Re-auth loop vô hạn nếu server chết | MEDIUM | Retry 5 lần → SetException; không retry vô hạn |
| Client config timeout quá thấp | LOW | Document khuyến nghị timeout ≥ 3 × interval |
