#if NET9_0
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MyConnection;

public class WebSocketListener
{
    private readonly ServerConfig _config;
    private readonly ServerTokenService _tokenService;
    private readonly ConnectionRegistry _registry;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _connectionTasks = new();

    public int Port => _listener is not null ? ((IPEndPoint)_listener.LocalEndpoint).Port : 0;

    public WebSocketListener(ServerConfig config, ServerTokenService tokenService, ConnectionRegistry registry)
    {
        _config = config;
        _tokenService = tokenService;
        _registry = registry;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var uri = new Uri($"http://{_config.websocketEndpoint}");
        var port = uri.Port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
                var task = Task.Run(() => HandleConnection(tcpClient), _cts.Token);
                lock (_connectionTasks)
                {
                    _connectionTasks.Add(task);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleConnection(TcpClient tcpClient)
    {
        try
        {
            using (tcpClient)
            {
                var stream = tcpClient.GetStream();
                var headers = await ReadHttpHeaders(stream);

                var authHeader = headers.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
                string? token = null;
                if (authHeader.Value is not null)
                {
                    var match = Regex.Match(authHeader.Value, @"^Bearer\s+(.+)$", RegexOptions.IgnoreCase);
                    if (match.Success)
                        token = match.Groups[1].Value;
                }

                if (token is null)
                {
                    await WriteHttpResponse(stream, 401, "Unauthorized");
                    return;
                }

                var principal = _tokenService.ValidateToken(token);
                if (principal is null)
                {
                    await WriteHttpResponse(stream, 401, "Unauthorized");
                    return;
                }

                var webSocketKey = headers.FirstOrDefault(h => h.Key.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)).Value;
                if (webSocketKey is null)
                {
                    await WriteHttpResponse(stream, 400, "Bad Request");
                    return;
                }

                var acceptKey = ComputeWebSocketAcceptKey(webSocketKey);
                await WriteWebSocketHandshake(stream, acceptKey);

                var webSocket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value ?? "";
                var userName = principal.FindFirst(ClaimTypes.Name)?.Value ?? principal.FindFirst("name")?.Value ?? "";
                var user = new JwtUser(userId, userName);

                var connection = new ConnectionImplement(webSocket, user);
                _registry.Register(connection);

                await ReceiveLoop(connection, webSocket);

                await connection.CloseAsync();
                _registry.Remove(connection.Id);
            }
        }
        catch (Exception)
        {
            // Connection closed or error, cleanup done in finally block
        }
    }

    private async Task ReceiveLoop(ConnectionImplement connection, WebSocket webSocket)
    {
        var buffer = new byte[1024 * 64];
        while (webSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            connection.WebSocketPingTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var data = ms.ToArray();
            try
            {
                var envelope = MessageEnvelope.Parser.ParseFrom(data);
                _registry.Route(connection.Id, envelope.Subject, envelope.Payload.ToByteArray(), fromUdp: false);
            }
            catch
            {
                // Invalid message, ignore
            }
        }
    }

    private static async Task<Dictionary<string, string>> ReadHttpHeaders(NetworkStream stream)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(requestLine))
            return headers;

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(line))
                break;
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim();
                headers[key] = value;
            }
        }
        return headers;
    }

    private static async Task WriteHttpResponse(NetworkStream stream, int statusCode, string statusText)
    {
        var response = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static string ComputeWebSocketAcceptKey(string webSocketKey)
    {
        var magicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(webSocketKey + magicGuid));
        return Convert.ToBase64String(hash);
    }

    private static async Task WriteWebSocketHandshake(NetworkStream stream, string acceptKey)
    {
        var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                       "Upgrade: websocket\r\n" +
                       "Connection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    public async ValueTask StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        Task[] tasks;
        lock (_connectionTasks)
        {
            tasks = _connectionTasks.ToArray();
            _connectionTasks.Clear();
        }
        await Task.WhenAll(tasks);

        foreach (var conn in _registry.GetAll())
        {
            if (conn is ConnectionImplement impl)
                await impl.CloseAsync();
        }
        _registry.Clear();
    }

    private class JwtUser : IUser
    {
        public string Name { get; }
        public string Id { get; }
        public JwtUser(string id, string name) { Id = id; Name = name; }
    }
}
#endif
