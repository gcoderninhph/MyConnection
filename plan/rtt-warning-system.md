# Plan: RTT Measurement + OnWarning System

## Overview

| Feature | Description |
|---------|-------------|
| **RTT** | `IClient.LatestRttMs` — measured automatically via existing UDP ping/pong cycle |
| **OnWarning** | `ISubscribe OnWarning(Action<WarningInfo>)` on both `IClient` + `IServer` |

---

## Phase 1 — `WarningInfo` class (NEW FILE)

**`src/impl/WarningInfo.cs`**

```csharp
namespace MyConnection;
public sealed class WarningInfo
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
```

---

## Phase 2 — RTT: `MessageEnvelope` add field #4 `long Ticks`

| Area | Change |
|------|--------|
| Property | `public long Ticks { get; set; }` |
| Clone ctor | Copy `Ticks` |
| `MergeFrom` | `case 4: Ticks = input.ReadInt64(); break;` |
| `WriteTo` | If `Ticks != 0`: `WriteRawTag(32)` + `WriteInt64(Ticks)` |
| `CalculateSize` | +1 + `CodedOutputStream.ComputeInt64Size(Ticks)` |
| `Equals`/`GetHashCode` | Include `Ticks` |

> Protobuf field #4 is backwards-compatible — old clients ignore unknown fields.

---

## Phase 3 — RTT: `UdpPingService` modifications

| Change | Detail |
|--------|--------|
| New field `_lastPingSentTicks` | `long` — Unix ms timestamp when ping was sent |
| `OnTick()` | Set `envelope.Ticks = now`, store `_lastPingSentTicks` |
| `OnPongReceived(long sentTicks)` | Signature change (was parameterless). If `sentTicks > 0`: `LatestRttMs = now - sentTicks`. Still update `_lastPongTime`. |
| `LatestRttMs` | `public long? LatestRttMs { get; private set; }` |

---

## Phase 4 — RTT: `UdpListener` echo

In `HandleDatagram`, when `__ping__` received:

```csharp
var pongEnvelope = new MessageEnvelope
{
    Subject = "__pong__",
    Ticks = envelope.Ticks  // echo back
};
```

Server simply echoes whatever `Ticks` value the client sent.

---

## Phase 5 — RTT: `ClientImplement`

- In `HandleUdpMessage`, `__pong__` handler: pass `envelope.Ticks` to `_udpPing.OnPongReceived(envelope.Ticks)`
- New property: `public long? LatestRttMs => _udpPing?.LatestRttMs;`
- `ClientAbstract`: add `public abstract long? LatestRttMs { get; }`
- `TestClient`: override returns `null` (UDP not implemented)

---

## Phase 6 — `SubjectDispatcher` empty-subscribers event

```csharp
public event Action<string>? OnEmptyDispatch;
```

In `Dispatch()`: when `_subscribers.TryGetValue(subject, out ...)` returns false → `OnEmptyDispatch?.Invoke(subject)`.

---

## Phase 7 — Interfaces: `OnWarning` + `LatestRttMs`

**`IClient.cs`** (+2 members):

```csharp
ISubscribe OnWarning(Action<WarningInfo> onWarning);
long? LatestRttMs { get; }
```

**`IServer.cs`** (+1 member):

```csharp
ISubscribe OnWarning(Action<WarningInfo> onWarning);
```

**`ClientAbstract.cs`** (+2 abstract members):

```csharp
public abstract ISubscribe OnWarning(Action<WarningInfo> onWarning);
public abstract long? LatestRttMs { get; }
```

**`ServerAbstract.cs`** (+1 abstract member):

```csharp
public abstract ISubscribe OnWarning(Action<WarningInfo> onWarning);
```

---

## Phase 8 — Client-side warning wiring (`ClientImplement`)

### Callback infrastructure

Same pattern as `_onDisconnectCallbacks`:

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

### Warning codes

| Code | Message | Trigger Point |
|------|---------|---------------|
| **W001** | `"UDP ping timeout, connection may be lost"` | `OnUdpPingTimeout` — after existing re-auth logic fires |
| **W002** | `"UDP handshake failed after 5 retries"` | `RunUdpHandshake` — after retry loop, before `TrySetException` |
| **W003** | `"TCP send failed, WebSocket not connected"` | `SendOnTcp` — if `_ws == null \|\| _ws.State != WebSocketState.Open` |
| **W004** | `"UDP send failed"` | `SendOnUdp` — `SendAsync` throws or `_udpClient` is null |
| **W005** | `"WebSocket closed unexpectedly (code: {code})"` | `_ws.OnClose` — after successful connect (not during connect) |
| **W006** | `"TCP message dropped, no subscriber for subject '{subject}'"` | Hook `_tcpDispatcher.OnEmptyDispatch` |
| **W007** | `"UDP message dropped, no subscriber for subject '{subject}'"` | Hook `_udpDispatcher.OnEmptyDispatch` |

### Wire-up locations

```
SendOnTcp:
  if (_ws == null || _ws.State != Open)
    => FireWarning("W003", "...");

SendOnUdp:
  await _udpReadyTcs.Task;
  try { await _udpClient.SendAsync(...); }
  catch (Exception ex) { FireWarning("W004", "...", ex); }

OnUdpPingTimeout:
  // After existing re-auth attempt:
  FireWarning("W001", "...");

RunUdpHandshake:
  // After retry loop, before TrySetException:
  FireWarning("W002", "...");

_ws.OnClose:
  if (connectTcs.Task.IsCompletedSuccessfully)
    FireWarning("W005", $"WebSocket closed (code: {code})");

Constructor / NotifyConnectUdp:
  _tcpDispatcher.OnEmptyDispatch += sub => FireWarning("W006", $"...{sub}");
  _udpDispatcher.OnEmptyDispatch += sub => FireWarning("W007", $"...{sub}");
```

---

## Phase 9 — Server-side warning wiring

### `ConnectionRegistry` — callback infrastructure

```csharp
private readonly List<Action<WarningInfo>> _onWarningCallbacks = new();

internal void FireWarning(string code, string message, Exception? ex = null)
{
    var info = new WarningInfo(code, message, ex);
    Action<WarningInfo>[] snapshot;
    lock (_onWarningCallbacks) { snapshot = _onWarningCallbacks.ToArray(); }
    foreach (var cb in snapshot) cb(info);
}

public ISubscribe OnWarning(Action<WarningInfo> callback)
{
    lock (_onWarningCallbacks) { _onWarningCallbacks.Add(callback); }
    return new UnsubscribeHandle(() => { lock (_onWarningCallbacks) { _onWarningCallbacks.Remove(callback); } });
}
```

### Server warning codes

| Code | Message | Trigger Point |
|------|---------|---------------|
| **W001** | `"UDP send failed, no endpoint bound for connection {id}"` | `ServerImplement.SendOnUdp` — catch `InvalidOperationException` |
| **W002** | `"UDP send to {id} failed"` | `ServerImplement.SendOnUdp` — catch generic `Exception` |
| **W003** | `"TCP send to {id} failed"` | `ServerImplement.SendOnTcp` — catch `Exception` |
| **W006** | `"TCP message dropped, no subscriber for subject '{subject}'"` | `ConnectionRegistry.Route(fromUdp: false)` — no subscribers |
| **W007** | `"UDP message dropped, no subscriber for subject '{subject}'"` | `ConnectionRegistry.Route(fromUdp: true)` — no subscribers |

### Wire-up in `ServerImplement`

```csharp
public override ISubscribe OnWarning(Action<WarningInfo> onWarning)
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
        _registry.FireWarning("W001", $"UDP send failed, no endpoint bound for connection {connection.Id}");
    }
    catch (Exception ex)
    {
        _registry.FireWarning("W002", $"UDP send to {connection.Id} failed", ex);
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
        _registry.FireWarning("W003", $"TCP send to {connection.Id} failed", ex);
    }
}
```

### Wire-up in `ConnectionRegistry.Route`

```csharp
public void Route(string connectionId, string subject, byte[] payload, bool fromUdp)
{
    var subscribers = !fromUdp ? _tcpSubscribers : _udpSubscribers;
    if (!subscribers.TryGetValue(subject, out var list))
    {
        var transport = fromUdp ? "UDP" : "TCP";
        FireWarning($"W{(fromUdp ? "007" : "006")}",
            $"{transport} message dropped, no subscriber for subject '{subject}'");
        return;
    }
    // ... existing iterate logic
}
```

---

## Phase 10 — Tests

| Test | What | How |
|------|------|-----|
| **D1** | `OnWarning` fires `W003` when sending TCP while disconnected | Connect → disconnect → `SendOnTcp` → assert warning |
| **D2** | `OnWarning` fires `W006` when sending TCP to subject with no subscribers | Connect → `SendOnTcp("no_sub", ...)` → assert `W006` warning with subject name |
| **D3** | Server `OnWarning` fires when sending to subject with no subscribers | Connect → server `SendOnTcp("no_sub", conn, ...)` → assert server `W006` |
| **D4** | `LatestRttMs` is `null` when UDP disabled | Assert `client.LatestRttMs is null` |

---

## Files Modified (12) + Created (1)

| # | File | Action |
|---|------|--------|
| 1 | `src/impl/WarningInfo.cs` | **NEW** |
| 2 | `src/impl/MessageEnvelope.cs` | Add protobuf field #4 `Ticks` |
| 3 | `src/impl/UdpPingService.cs` | RTT tracking + `LatestRttMs` |
| 4 | `src/impl/UdpListener.cs` | Echo `Ticks` in pong |
| 5 | `src/impl/SubjectDispatcher.cs` | Add `OnEmptyDispatch` event |
| 6 | `src/impl/ClientImplement.cs` | Warning callbacks + `LatestRttMs` |
| 7 | `src/impl/ConnectionRegistry.cs` | Warning callbacks + route warnings |
| 8 | `src/impl/ServerImplement.cs` | Warning on sends + expose `OnWarning` |
| 9 | `src/IClient.cs` | +`OnWarning` + `LatestRttMs` |
| 10 | `src/IServer.cs` | +`OnWarning` |
| 11 | `src/ClientAbstract.cs` | +2 abstract members |
| 12 | `src/ServerAbstract.cs` | +1 abstract member |
| 13 | `MyConnection.Tests/ConnectionTests.cs` | +4 tests + `TestClient` overrides |
