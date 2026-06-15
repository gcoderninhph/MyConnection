# Client SubjectDispatcher Split Plan

## Problem

`ClientImplement` dùng **1 `SubjectDispatcher`** chung cho cả TCP và UDP:

```
OnMessage (WS/TCP)     ─┐
                         ├─► _dispatcher.Dispatch("echo") ► fire ALL "echo" subscribers
HandleUdpMessage (UDP)  ─┘
```

```csharp
// Current (ClientImplement.cs)
private readonly SubjectDispatcher _dispatcher = new();

// OnMessage → dispatch
_dispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());

// HandleUdpMessage → dispatch
_dispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());

// SubscribeTcp / SubscribeUdp → cả 2 vào cùng dispatcher
public override ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data)
    => _dispatcher.Subscribe(subject, data);
public override ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data)
    => _dispatcher.Subscribe(subject, data);
```

**Hậu quả**: Khi server reply 1 lần qua TCP, `OnMessage` dispatch → fire cả `SubscribeTcp` handler VÀ `SubscribeUdp` handler. Client thấy 2 callback dù chỉ có 1 reply. Tương tự với UDP.

Server đã fix cùng vấn đề này bằng cách tách `_tcpSubscribers` / `_udpSubscribers` trong `ConnectionRegistry` (plan `plan/udp-plan.md` → `ConnectionRegistry` split).

---

## Solution

Tách `_dispatcher` thành `_tcpDispatcher` + `_udpDispatcher`.

| Method | Current | After |
|--------|---------|-------|
| `OnMessage` (WS) | `_dispatcher.Dispatch(...)` | `_tcpDispatcher.Dispatch(...)` |
| `HandleUdpMessage` | `_dispatcher.Dispatch(...)` | `_udpDispatcher.Dispatch(...)` |
| `SubscribeTcp` | `_dispatcher.Subscribe(...)` | `_tcpDispatcher.Subscribe(...)` |
| `SubscribeUdp` | `_dispatcher.Subscribe(...)` | `_udpDispatcher.Subscribe(...)` |

### Files

**1 file**: `src/impl/ClientImplement.cs`

### Changes

#### 1. Field declaration

```diff
- private readonly SubjectDispatcher _dispatcher = new();
+ private readonly SubjectDispatcher _tcpDispatcher = new();
+ private readonly SubjectDispatcher _udpDispatcher = new();
```

#### 2. `OnMessage` (line ~49, WebSocket callback)

`OnMessage` handles both user messages and internal WS messages (`__udp_auth__`, `__udp_bound__`). Internal messages use `_udpAuthKeyTcs`/`_udpReadyTcs` and `return` early. User messages currently fall through to:

```diff
- _dispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
+ _tcpDispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
```

#### 3. `HandleUdpMessage` (line ~180)

Handles `__pong__` (internal → early `return`). User UDP messages fall through to:

```diff
- _dispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
+ _udpDispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
```

#### 4. `SubscribeTcp` / `SubscribeUdp` (lines ~295, ~298)

```diff
  public override ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data)
-     => _dispatcher.Subscribe(subject, data);
+     => _tcpDispatcher.Subscribe(subject, data);

  public override ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data)
-     => _dispatcher.Subscribe(subject, data);
+     => _udpDispatcher.Subscribe(subject, data);
```

---

## Impact Assessment

- **Breaking change**: Không (public API không đổi, chỉ internal behavior sửa)
- **Server**: Không bị ảnh hưởng (đã fix riêng)
- **Tests**: Các test dùng `SubscribeTcp` → chỉ ảnh hưởng đúng TCP dispatcher, không cần sửa test
- **Unity client**: Sau update, `SubscribeTcp` chỉ nhận TCP message, `SubscribeUdp` chỉ nhận UDP message (đúng ý)

---

## Verification

1. Build `dotnet build`
2. Run existing tests `dotnet test`
3. Unity client: gửi 1 TCP message → chỉ thấy 1 log "Server send TCP"
4. Unity client: gửi 1 UDP message → chỉ thấy 1 log "Server send UDP"
5. NuGet rebuild `dotnet pack`
