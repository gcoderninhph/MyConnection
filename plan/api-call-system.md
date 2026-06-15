# Plan: Sửa Implementation + Bổ Sung API Request System

## Tổng quan

Sau khi thay đổi Interface và Config:
- `IClient.Create(ClientConfig)` — config gắn vào lúc tạo, không còn ở `ConnectServer`
- `ConnectServer()` không nhận tham số
- `tcpServer` (host:port) tách khỏi `websocketEnpoint` (path)
- Thêm `Login`, `GetRequest`, `PostRequest` vào `IClient`
- Thêm `OnLogin`, `OnGetRequest`, `OnPostRequest` vào `IServer`
- Thêm `restEndpoint` + `tcpPort` vào config

Cần sửa toàn bộ code triển khai khớp với interface/config mới, đồng thời bổ sung tính năng gọi API qua REST endpoint.

**Thêm `where T : IMessage` cho tất cả generic method** trong interface và toàn bộ chain implementation.

---

## Giai đoạn 1: Thêm `where IMessage` constraint — Interface

### 1.1 IClient.cs

Thêm `using Google.Protobuf;` vào đầu file.

Thêm constraint `where TData : IMessage` cho:

| Method | Constraint |
|---|---|
| `SendOnUdp<TData>` | `where TData : IMessage` |
| `SendOnTcp<TData>` | `where TData : IMessage` |
| `SubscribeUdp<TData>` | `where TData : IMessage` |
| `SubscribeTcp<TData>` | `where TData : IMessage` |
| `Login<TData>` (2 overload) | `where TData : IMessage` |

Thêm constraint cho method mới:

| Method | Constraint |
|---|---|
| `GetRequest<TResponse>` | `where TResponse : IMessage` |
| `PostRequest<TRequest, TResponse>` | `where TRequest : IMessage where TResponse : IMessage` |

**Lưu ý**: `Login` return type là `Task<IUser>` — không generic về return. `IUser` không cần `IMessage`.

### 1.2 IServer.cs

Thêm `using Google.Protobuf;` vào đầu file.

Thêm constraint `where TData : IMessage` cho:

| Method | Constraint |
|---|---|
| `SendOnUdp<TData>` | `where TData : IMessage` |
| `SendOnTcp<TData>` | `where TData : IMessage` |
| `SendAllOnUdp<TData>` | `where TData : IMessage` |
| `SendAllOnTcp<TData>` | `where TData : IMessage` |
| `SubscribeUdp<TData>` | `where TData : IMessage` |
| `SubscribeTcp<TData>` | `where TData : IMessage` |

Thêm constraint cho method mới:

| Method | Constraint |
|---|---|
| `OnLogin<TData>` | `where TData : IMessage` |
| `OnGetRequest<TResponse>` | `where TResponse : IMessage` |
| `OnPostRequest<TRequest, TResponse>` | `where TRequest : IMessage where TResponse : IMessage` |

---

## Giai đoạn 2: Lan truyền constraint + sửa config — Abstract

### 2.1 ClientAbstract.cs

**2.1.1** Thêm `protected ClientConfig _config` — field để subclass truy cập config.

**2.1.2** `ConnectServer(ClientConfig config)` → `ConnectServer()`:
- Bỏ tham số `config`, dùng `_config` có sẵn
- Đọc `_config.tcpServer`, `_config.websocketEnpoint`, `_config.tcpSecurity`, `_config.udpServer`
- Gọi `ConnectWebSocket(token, fullWsUrl)` và `NotifyConnectUdp(token, udpServer)` như cũ

**2.1.3** Thêm constraint `where TData : IMessage` cho:
- `SendOnUdp<TData>`, `SendOnTcp<TData>`
- `SubscribeUdp<TData>`, `SubscribeTcp<TData>`

**2.1.4** Thêm abstract member mới:
- `abstract bool IsConnected { get; }`
- `abstract Task<IUser> Login<TData>(Func<TData>) where TData : IMessage`
- `abstract Task<IUser> Login<TData>(Func<Task<TData>>) where TData : IMessage`
- `abstract Task<TResponse> GetRequest<TResponse>(string) where TResponse : IMessage`
- `abstract Task<TResponse> PostRequest<TRequest, TResponse>(string, TRequest) where TRequest : IMessage where TResponse : IMessage`

### 2.2 ServerAbstract.cs

**2.2.1** Thêm constraint `where TData : IMessage` cho:
- `SendOnUdp<TData>`, `SendOnTcp<TData>`
- `SendAllOnUdp<TData>`, `SendAllOnTcp<TData>`
- `SubscribeUdp<TData>`, `SubscribeTcp<TData>`

**2.2.2** Thêm abstract member mới:
- `abstract void OnLogin<TData>(Func<TData, Task<IUser>>) where TData : IMessage`
- `abstract void OnGetRequest<TResponse>(string, Func<Task<TResponse>>) where TResponse : IMessage`
- `abstract void OnPostRequest<TRequest, TResponse>(string, Func<TRequest, Task<TResponse>>) where TRequest : IMessage where TResponse : IMessage`

---

## Giai đoạn 3: Sửa ClientImplement — Config + Constraint

### 3.1 Constructor

```csharp
public ClientImplement(ClientConfig config)
{
    _config = config;
    _udpPingIntervalMs = config.udpPingIntervalMs > 0 ? config.udpPingIntervalMs : 5000;
    _udpPingTimeoutMs = config.udpPingTimeoutMs > 0 ? config.udpPingTimeoutMs : 15000;
}
```

### 3.2 ConnectWebSocket — ghép URL từ config

URL construction:
```
ws{s}://   — nếu tcpSecurity=true → wss://, else ws://
{tcpServer} — "127.0.0.1:9090"
{websocketEnpoint} — "/ws"
→ ws://127.0.0.1:9090/ws
```

Thay đổi logic hiện tại: bỏ `websocketServer` parameter, thay bằng đọc từ `_config`.

### 3.3 NotifyConnectUdp — đọc từ config

Bỏ `token` và `udpServer` parameter, đọc `_config.udpServer`.

### 3.4 IsConnected property

```csharp
public override bool IsConnected => _ws?.State == WebSocketState.Open;
```

### 3.5 Thêm constraint toàn bộ override

- `SendOnUdp<TData>` → `where TData : IMessage`
- `SendOnTcp<TData>` → `where TData : IMessage`
- `SubscribeUdp<TData>` → `where TData : IMessage`
- `SubscribeTcp<TData>` → `where TData : IMessage`
- Bỏ runtime cast `(message as IMessage)` trong `ProtoSerializer` nếu có → compiler đảm bảo rồi

---

## Giai đoạn 4: Sửa WebSocketListener — Port + REST routing

### 4.1 Sửa StartAsync — dùng `_config.tcpPort`

```csharp
// Cũ:
var uri = new Uri($"http://{_config.websocketEndpoint}");
var port = uri.Port;

// Mới:
var port = _config.tcpPort;
```

`_config.tcpPort` mặc định 9090, server bind trên port đó. `websocketEndpoint` chỉ còn là path `/ws` để match request.

### 4.2 Phân luồng HTTP request trong HandleConnection

Sau khi đọc headers, cần phân biệt WS upgrade vs REST request:

```
Đọc requestLine + headers (như cũ, nhưng lưu lại method + path)
├─ Path == _config.websocketEndpoint ("/ws")
│  └─ Có Sec-WebSocket-Key → WS upgrade (logic cũ)
│     └─ Không có key → 400 Bad Request
│
├─ Path == _config.restEndpoint ("/api")
│  └─ Method POST → đọc body → parse ApiRequest → gọi REST handler → trả HTTP response
│     └─ Method khác → 405 Method Not Allowed
│
└─ Path khác → 404 Not Found
```

### 4.3 Sửa ReadHttpHeaders — lưu method + path

Cập nhật `ReadHttpHeaders` trả về thêm `(method, path)` từ request line:

```
Cũ: private static async Task<Dictionary<string, string>> ReadHttpHeaders(NetworkStream)
Mới: private static async Task<(string method, string path, Dictionary<string, string> headers)> ReadHttpRequest(NetworkStream)
```

Parse request line `POST /api HTTP/1.1` → method=`POST`, path=`/api`.

### 4.4 Đọc HTTP body

Sau khi đọc headers, nếu có `Content-Length`:
```
int contentLength = int.Parse(headers["Content-Length"]);
byte[] body = new byte[contentLength];
await stream.ReadExactlyAsync(body, 0, contentLength);
```

### 4.5 Nhận REST handler delegate

Thêm constructor parameter để nhận delegate từ `ServerImplement`:
```csharp
private readonly Func<ApiRequest, Task<ApiResponse>>? _restHandler;

public WebSocketListener(ServerConfig config, ServerTokenService tokenService, 
    ConnectionRegistry registry, Func<ApiRequest, Task<ApiResponse>>? restHandler)
{
    ...
    _restHandler = restHandler;
}
```

### 4.6 Xử lý REST request

```csharp
// Trong HandleConnection, khi path == _config.restEndpoint:
var apiRequest = ApiRequest.Parser.ParseFrom(body);
var apiResponse = _restHandler is not null 
    ? await _restHandler(apiRequest) 
    : new ApiResponse { Success = false, ErrorCode = "NotImplemented" };
var responseBytes = apiResponse.ToByteArray();
await WriteHttpResponseWithBody(stream, 200, "OK", responseBytes);
```

### 4.7 Thêm WriteHttpResponseWithBody

Viết HTTP response kèm body:
```csharp
private static async Task WriteHttpResponseWithBody(NetworkStream stream, int statusCode, 
    string statusText, byte[] body)
{
    var header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                 $"Content-Type: application/octet-stream\r\n" +
                 $"Content-Length: {body.Length}\r\n" +
                 $"Connection: close\r\n\r\n";
    var headerBytes = Encoding.ASCII.GetBytes(header);
    await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
    await stream.WriteAsync(body, 0, body.Length);
}
```

---

## Giai đoạn 5: Sửa ServerImplement — OnLogin/OnGetRequest/OnPostRequest

### 5.1 Handler storage

```csharp
private Func<byte[], Task<byte[]>>? _loginHandler;
private readonly Dictionary<string, Func<byte[], Task<byte[]>>> _getHandlers = new();
private readonly Dictionary<string, Func<byte[], Task<byte[]>>> _postHandlers = new();
```

Dùng `Func<byte[], Task<byte[]>>` để type-erased storage — handler nhận raw bytes, trả raw bytes.

### 5.2 OnLogin<TData>(Func<TData, Task<IUser>>)

```csharp
public override void OnLogin<TData>(Func<TData, Task<IUser>> authLogic) where TData : IMessage
{
    _loginHandler = async (payload) =>
    {
        var data = ProtoSerializer.Deserialize<TData>(payload);
        var user = await authLogic(data);
        var token = _tokenService.CreateToken(user.Id, user.Name);
        var loginResponse = new LoginResponse
        {
            Token = token,
            UserId = user.Id,
            UserName = user.Name
        };
        return loginResponse.ToByteArray();
    };
}
```

### 5.3 OnGetRequest<TResponse>(string, Func<Task<TResponse>>)

```csharp
public override void OnGetRequest<TResponse>(string subject, Func<Task<TResponse>> requestLogic) 
    where TResponse : IMessage
{
    _getHandlers[subject] = async (_) =>
    {
        var result = await requestLogic();
        return ProtoSerializer.Serialize(result);
    };
}
```

### 5.4 OnPostRequest<TRequest, TResponse>(string, Func<TRequest, Task<TResponse>>)

```csharp
public override void OnPostRequest<TRequest, TResponse>(string subject, 
    Func<TRequest, Task<TResponse>> requestLogic) 
    where TRequest : IMessage where TResponse : IMessage
{
    _postHandlers[subject] = async (payload) =>
    {
        var request = ProtoSerializer.Deserialize<TRequest>(payload);
        var result = await requestLogic(request);
        return ProtoSerializer.Serialize(result);
    };
}
```

### 5.5 REST dispatch delegate

```csharp
private async Task<ApiResponse> HandleRestRequest(ApiRequest request)
{
    try
    {
        // Validate token (skip for login)
        if (request.Subject != "__login__")
        {
            var principal = _tokenService.ValidateToken(request.Token);
            if (principal is null)
                return new ApiResponse { Success = false, ErrorCode = "TokenExpired", ErrorMessage = "Token không hợp lệ hoặc đã hết hạn" };
        }

        // Giải nén nếu cần
        byte[] payload = request.Payload.ToByteArray();
        if (request.Compressed)
        {
            payload = Decompress(payload); // Zip decompress
        }

        // Route
        Func<byte[], Task<byte[]>>? handler = null;
        if (request.Subject == "__login__" && request.HasPayload)
        {
            handler = _loginHandler;
        }
        else if (!request.HasPayload)
        {
            _getHandlers.TryGetValue(request.Subject ?? "", out handler);
        }
        else
        {
            _postHandlers.TryGetValue(request.Subject ?? "", out handler);
        }

        if (handler is null)
            return new ApiResponse { Success = false, ErrorCode = "NotFound", ErrorMessage = $"Không tìm thấy handler cho subject '{request.Subject}'" };

        var result = await handler(payload);
        return new ApiResponse
        {
            Subject = request.Subject,
            Payload = Google.Protobuf.ByteString.CopyFrom(result),
            Success = true
        };
    }
    catch (Exception ex)
    {
        return new ApiResponse { Success = false, ErrorCode = "InternalError", ErrorMessage = ex.Message };
    }
}
```

### 5.6 Truyền delegate vào WebSocketListener

```csharp
_listener = new WebSocketListener(
    config, _tokenService, _registry, HandleRestRequest);
```

---

## Giai đoạn 6: Proto messages mới

### 6.1 ApiRequest.cs

Hand-written `IMessage<ApiRequest>`, field layout:

| Proto# | Name | Wire Type | Type |
|---|---|---|---|
| 1 | Subject | Length-delimited | `string` |
| 2 | Payload | Length-delimited | `ByteString` |
| 3 | Compressed | Varint | `bool` |
| 4 | HasPayload | Varint | `bool` |
| 5 | Token | Length-delimited | `string` |

Viết theo pattern của `MessageEnvelope.cs`: `MergeFrom(CodedInputStream)`, `WriteTo(CodedOutputStream)`, `CalculateSize()`, `Parser`, `Clone()`, `Equals`.

### 6.2 ApiResponse.cs

Hand-written `IMessage<ApiResponse>`, field layout:

| Proto# | Name | Wire Type | Type |
|---|---|---|---|
| 1 | Subject | Length-delimited | `string` |
| 2 | Payload | Length-delimited | `ByteString` |
| 3 | Compressed | Varint | `bool` |
| 4 | Success | Varint | `bool` |
| 5 | ErrorCode | Length-delimited | `string` |
| 6 | ErrorMessage | Length-delimited | `string` |

### 6.3 LoginResponse.cs

Hand-written `IMessage<LoginResponse>`, field layout:

| Proto# | Name | Wire Type | Type |
|---|---|---|---|
| 1 | Token | Length-delimited | `string` |
| 2 | UserId | Length-delimited | `string` |
| 3 | UserName | Length-delimited | `string` |

---

## Giai đoạn 7: Client HTTP — Login/GetRequest/PostRequest

### 7.1 Internal state cần thêm vào ClientImplement

```csharp
private HttpClient? _http;                    // HttpClient dùng chung
private string? _token;                       // JWT token sau khi login
private Func<Task<byte[]>>? _reLoginFactory;  // Closure để auto re-login
private object? _loginDataFactory;            // Giữ tham chiếu data() để không bị GC
```

### 7.2 HttpClient initialization

Trong `ConnectServer()` hoặc lazy-init:
```csharp
_http = new HttpClient();
_http.BaseAddress = new Uri($"http{(_config.tcpSecurity ? "s" : "")}://{_config.tcpServer}{_config.restEndpoint}");
_http.DefaultRequestHeaders.Accept.Clear();
_http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
_http.Timeout = TimeSpan.FromSeconds(30);
```

### 7.3 Login<TData>(Func<TData>)

```
1. Gọi data() → TData
2. Serialize TData → byte[]
3. Tạo ApiRequest { Subject="__login__", Payload=..., HasPayload=true, Compressed=false, Token="" }
4. Gọi SendApiRequest(apiRequest) → ApiResponse
5. Nếu Success:
   a. Deserialize Payload → LoginResponse
   b. Lưu _token = loginResponse.Token
   c. Lưu _reLoginFactory = closure: serialize data() → gửi login request → trả token
   d. Trả về UserInfo { Id=loginResponse.UserId, Name=loginResponse.UserName }
6. Nếu !Success → throw new ApiException(response.ErrorCode, response.ErrorMessage)
```

### 7.4 Login<TData>(Func<Task<TData>>)

Tương tự nhưng gọi `await data()` thay vì `data()`.

### 7.5 GetRequest<TResponse>(string subject)

```
1. Tạo ApiRequest { Subject=subject, HasPayload=false, Token=_token ?? "" }
2. Gọi SendApiRequestWithRetry(request) → ApiResponse
3. Deserialize Payload → TResponse
```

### 7.6 PostRequest<TRequest, TResponse>(string subject, TRequest body)

```
1. Serialize body → byte[]
2. Tạo ApiRequest { Subject=subject, Payload=..., HasPayload=true, Token=_token ?? "" }
3. Nếu _config.restCompressedEnable → nén Payload, set Compressed=true
4. Gọi SendApiRequestWithRetry(request) → ApiResponse
5. Deserialize Payload → TResponse
```

### 7.7 SendApiRequest + Auto Re-login

```csharp
private async Task<ApiResponse> SendApiRequestWithRetry(ApiRequest request)
{
    var response = await SendApiRequest(request);
    if (!response.Success && response.ErrorCode == "TokenExpired" && _reLoginFactory is not null)
    {
        // Auto re-login
        var loginPayload = await _reLoginFactory();
        var loginRequest = new ApiRequest 
        { 
            Subject = "__login__", 
            Payload = ByteString.CopyFrom(loginPayload), 
            HasPayload = true 
        };
        var loginResponse = await SendApiRequest(loginRequest);
        if (!loginResponse.Success)
            throw new ApiException(loginResponse.ErrorCode, loginResponse.ErrorMessage);
        
        var loginResult = LoginResponse.Parser.ParseFrom(loginResponse.Payload);
        _token = loginResult.Token;
        
        // Retry original request with new token
        request.Token = _token;
        response = await SendApiRequest(request);
    }
    if (!response.Success)
        throw new ApiException(response.ErrorCode, response.ErrorMessage);
    return response;
}
```

### 7.8 SendApiRequest (raw HTTP)

```csharp
private async Task<ApiResponse> SendApiRequest(ApiRequest request)
{
    var requestBytes = request.ToByteArray();
    var content = new ByteArrayContent(requestBytes);
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    var httpResponse = await _http!.PostAsync("", content);
    var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync();
    return ApiResponse.Parser.ParseFrom(responseBytes);
}
```

### 7.9 Internal UserInfo class

```csharp
internal class UserInfo : IUser
{
    public string Name { get; }
    public string Id { get; }
    public UserInfo(string id, string name) { Id = id; Name = name; }
}
```

---

## Giai đoạn 8: ApiException

### 8.1 src/impl/ApiException.cs

```csharp
namespace MyConnection;

public class ApiException : Exception
{
    public string ErrorCode { get; }
    public ApiException(string errorCode, string errorMessage) : base(errorMessage)
    {
        ErrorCode = errorCode;
    }
}
```

---

## Giai đoạn 9: Sửa SubjectDispatcher — thêm constraint

### 9.1 Subscribe + Dispatch

```csharp
// Subscribe
public ISubscribe Subscribe<TData>(string subject, Action<TData> data) where TData : IMessage

// Dispatch  
public void Dispatch(string subject, byte[] rawPayload)
```

`Dispatch` không cần generic vì nhận `byte[]` raw.

### 9.2 Cập nhật call site

- `ClientImplement.SubscribeTcp<TData>` / `SubscribeUdp<TData>` gọi `_tcpDispatcher.Subscribe<TData>(subject, callback)` / `_udpDispatcher.Subscribe<TData>(subject, callback)` — constraint đã được lan truyền.

---

## Giai đoạn 10: Sửa ConnectionRegistry — thêm constraint

### 10.1 SubscribeTcpLocal + SubscribeUdpLocal

```csharp
public ISubscribe SubscribeTcpLocal<TData>(string subject, Action<IConnection, TData> callback) 
    where TData : IMessage

public ISubscribe SubscribeUdpLocal<TData>(string subject, Action<IConnection, TData> callback) 
    where TData : IMessage
```

### 10.2 Cập nhật ServerImplement call site

Đã gọi `_registry.SubscribeTcpLocal<TData>` / `_registry.SubscribeUdpLocal<TData>` → constraint tự lan truyền từ interface.

---

## Giai đoạn 11: Sửa ProtoSerializer — bỏ runtime cast, thay Activator

### 11.1 Serialize<T>

```csharp
// Cũ:
var msg = (message as IMessage) ?? throw new InvalidOperationException(...);

// Mới: constraint đảm bảo, gọi trực tiếp
public static byte[] Serialize<T>(T message) where T : IMessage
{
    return message.ToByteArray();
}
```

### 11.2 Deserialize<T> — Dùng MessageParser (IL2CPP-safe)

```csharp
public static T Deserialize<T>(byte[] data) where T : IMessage
{
    return new MessageParser<T>(() => (T)Activator.CreateInstance(typeof(T))).ParseFrom(data);
}
```

Lý do: `MessageParser<T>` của Google.Protobuf đã internalize pattern AOT-safe. Mặc dù vẫn dùng `Activator` bên trong, nhưng parser được cache và Google.Protobuf đã test IL2CPP. Không cần `link.xml` nếu dùng đúng path này (thay vì gọi `Activator.CreateInstance<T>()` trực tiếp).

---

## Giai đoạn 12: Sửa file test — ConnectionTests.cs

### 12.1 MakeClientConfig cũ dùng `token`/`websocketServer`

```csharp
// Cũ:
private ClientConfig MakeConfig(string token)
    => new()
    {
        token = token,
        websocketServer = $"127.0.0.1:{_port}/ws",
        udpServer = ""
    };

// Mới:
private ClientConfig MakeConfig(string token)
    => new()
    {
        tcpServer = $"127.0.0.1:{_port}",
        websocketEnpoint = "/ws",
        udpServer = ""
    };
```

### 12.2 Test Client nếu có dùng ClientImplement cũ

Nếu test nào tạo `new ClientImplement()` không tham số → sửa thành `new ClientImplement(config)`.

### 12.3 Test D1-D4

Kiểm tra các test RTT/Warning vẫn pass sau khi sửa constraint + config.

---

## Giai đoạn 13: Build & Verify

| # | Lệnh | Mục tiêu |
|---|---|---|
| 13.1 | `dotnet build` | Build cả `net9.0` và `netstandard2.1` |
| 13.2 | `dotnet test` | Chạy test, xác nhận D1-D4 + các test cũ pass |
| 13.3 | `dotnet pack -o nupkgs` | Đóng gói NuGet |
| 13.4 | `dotnet publish ConsoleDemo -c Release --self-contained true -r win-x64` | Build ConsoleDemo exe |

---

## Thứ tự triển khai

```
[1]  IClient.cs          — thêm using + constraint + method mới
[2]  IServer.cs          — thêm using + constraint + method mới
[3]  ClientAbstract.cs    — constraint + ConnectServer không param + abstract mới
[4]  ServerAbstract.cs    — constraint + abstract mới
[5]  SubjectDispatcher.cs — thêm constraint cho Subscribe
[6]  ProtoSerializer.cs   — thêm constraint, bỏ runtime cast
[7]  ApiRequest.cs        — proto message mới
[8]  ApiResponse.cs       — proto message mới
[9]  LoginResponse.cs     — proto message mới
[10] ApiException.cs      — class mới
[11] ConnectionRegistry.cs — thêm constraint SubscribeTcpLocal/SubscribeUdpLocal
[12] WebSocketListener.cs — port từ tcpPort, phân luồng REST, đọc body, gọi handler
[13] ServerImplement.cs   — constraint + OnLogin/OnGetRequest/OnPostRequest + dispatch
[14] ClientImplement.cs   — constructor config + constraint + IsConnected + Login/GetRequest/PostRequest + HttpClient + auto re-login
[15] ConnectionTests.cs   — sửa MakeClientConfig + test mới nếu cần
[16] Build + Test + Pack + Publish
```

---

## File bị ảnh hưởng (change list)

| File | Thay đổi |
|---|---|
| `src/IClient.cs` | +using, +where IMessage, +Login/GetRequest/PostRequest |
| `src/IServer.cs` | +using, +where IMessage, +OnLogin/OnGetRequest/OnPostRequest |
| `src/ClientAbstract.cs` | +_config, sửa ConnectServer, +constraint, +abstract mới |
| `src/ServerAbstract.cs` | +constraint, +abstract mới |
| `src/impl/SubjectDispatcher.cs` | +constraint Subscribe |
| `src/impl/ProtoSerializer.cs` | +constraint Serialize/Deserialize, bỏ cast |
| `src/impl/ApiRequest.cs` | **MỚI** |
| `src/impl/ApiResponse.cs` | **MỚI** |
| `src/impl/LoginResponse.cs` | **MỚI** |
| `src/impl/ApiException.cs` | **MỚI** |
| `src/impl/ConnectionRegistry.cs` | +constraint SubscribeTcpLocal/SubscribeUdpLocal |
| `src/impl/WebSocketListener.cs` | Sửa port, phân luồng REST, đọc body, +restHandler |
| `src/impl/ServerImplement.cs` | +constraint, +handler storage, +dispatch, truyền restHandler |
| `src/impl/ClientImplement.cs` | +ctor config, +constraint, +IsConnected, +Login/GetRequest/PostRequest, +HttpClient, +auto re-login |
| `MyConnection.Tests/ConnectionTests.cs` | Sửa MakeClientConfig |

---

## Ghi chú

- **Login không cần connect trước**: `Login` gửi HTTP trực tiếp tới REST endpoint, không qua WebSocket. Token nhận được dùng cho `ConnectServer()` và các `GetRequest`/`PostRequest` sau này.
- **Compressed hỗ trợ trong PostRequest**: Nếu `_config.restCompressedEnable = true`, body `TRequest` được nén zip trước khi gửi, server tự động giải nén nếu `ApiRequest.Compressed = true`.
- **ProtoSerializer.Deserialize** dùng `MessageParser<T>(factory).ParseFrom(data)` thay vì `Activator.CreateInstance<T>()` trực tiếp — Google.Protobuf đảm bảo AOT/IL2CPP-safe. Xem chi tiết ở Giai đoạn 11 và mục Tương thích IL2CPP.
- **HttpClient** có sẵn trong netstandard2.1 và NET9_0, không cần NuGet thêm. Unity hỗ trợ HttpClient trên Standalone (Win/Mac/Linux), iOS, Android từ 2020.x+. WebGL không hỗ trợ — nằm ngoài scope của dự án.
- **Path matching**: `websocketEndpoint` và `restEndpoint` được so sánh chính xác (case-sensitive). Server không phục vụ static file hay route động — chỉ match exact path.

---

## Tương thích Unity / IL2CPP

### Phạm vi hỗ trợ

| Platform | WebSocket (NativeWebSocket) | REST API (HttpClient) | UDP Ping |
|---|---|---|---|
| **Standalone** (Win/Mac/Linux) | OK | OK | OK |
| **iOS** | OK | OK | OK |
| **Android** | OK | OK | OK |
| **WebGL** | Không hỗ trợ REST | — | Không hỗ trợ UDP |

WebGL nằm ngoài scope: `HttpClient` phụ thuộc `SocketsHttpHandler` không khả dụng trong browser sandbox. Nếu sau này cần WebGL thì phải thêm abstract `IHttpTransport` với implementation `UnityWebRequest` riêng.

### Vấn đề IL2CPP — `Activator.CreateInstance<T>()`

`ProtoSerializer.Deserialize<T>` hiện dùng `Activator.CreateInstance<T>()` để tạo instance trước khi `MergeFrom`. IL2CPP (AOT compiler trên iOS, Android, console) có thể strip parameterless constructor nếu static analysis không thấy reference trực tiếp.

**Giải pháp**: thay `Activator.CreateInstance<T>()` bằng `MessageParser<T>` của Google.Protobuf:

```csharp
// Cũ:
var instance = Activator.CreateInstance<T>();

// Mới:
private static readonly System.Reflection.PropertyInfo? s_parserProp = null;

public static T Deserialize<T>(byte[] data) where T : IMessage
{
    // Dùng MessageParser<T>.ParseFrom(codeInputStream) — Google.Protobuf đã lo AOT-safe
    var parser = MessageParser<T>.CreateUsingReflection();
    var instance = parser.ParseFrom(data);
    return instance;
}
```

Hoặc đơn giản hơn — gọi `.MergeFrom()` trên instance lấy từ `MessageParser`:

```csharp
public static byte[] Serialize<T>(T message) where T : IMessage
{
    return message.ToByteArray();
}

public static T Deserialize<T>(byte[] data) where T : IMessage
{
    var parser = new MessageParser<T>(() => (T)Activator.CreateInstance(typeof(T)));
    return parser.ParseFrom(data);
}
```

Nếu giữ `Activator`, cần thêm `link.xml` preserve cho tất cả IMessage type:

```xml
<linker>
  <assembly fullname="MyConnection">
    <type fullname="MyConnection.ApiRequest" preserve="all" />
    <type fullname="MyConnection.ApiResponse" preserve="all" />
    <type fullname="MyConnection.LoginResponse" preserve="all" />
    <type fullname="MyConnection.MessageEnvelope" preserve="all" />
  </assembly>
</linker>
```

**Khuyến nghị**: Dùng `MessageParser<T>` — Google.Protobuf đã internalize AOT-safe pattern, không cần link.xml.

### Vấn đề IL2CPP — Generic Virtual Method

`#if NET9_0` / `#if !NET9_0` gated code trong `UdpClientWrapper` đã xử lý variance `ReceiveAsync`. Không có thêm vấn đề gì với generic `where T : IMessage` vì IL2CPP sẽ preserve concrete instantiation miễn là method được gọi ở đâu đó trong code người dùng.

### Vấn đề IL2CPP — Compression

`System.IO.Compression.DeflateStream` / `GZipStream` hoạt động trên IL2CPP Standalone, iOS, Android. Dùng `using System.IO.Compression;` bình thường.

### JWT token

Client chỉ lưu token string và gửi kèm trong `ApiRequest.Token`. Không parse, không validate. Không cần thư viện JWT nào ở phía client → không vấn đề gì với IL2CPP.
