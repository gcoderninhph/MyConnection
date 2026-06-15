using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MyConnection.Tests;

public class ConnectionTests : IAsyncLifetime
{
    private ServerConfig _serverConfig = null!;
    private ServerImplement _server = null!;
    private int _port;
    private readonly List<TestClient> _clients = new();

    public Task InitializeAsync()
    {
        _serverConfig = new ServerConfig
        {
            websocketEndpoint = "127.0.0.1:0/ws",
            jwtSecret = "test-secret-key-at-least-thirty-two-bytes-long!!",
            jwtIssuer = "test-issuer",
            jwtAudience = "test-audience"
        };
        _server = (ServerImplement)ServerImplement.Create(_serverConfig);
        _port = _server.WebSocketPort;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients)
            await c.CloseAsync();
        _clients.Clear();
        try { await _server.DisposeAsync(); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────

    private string CreateToken(string id, string name)
        => _server.CreateToken(id, name);

    private string CreateExpiredToken(string id, string name)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_serverConfig.jwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _serverConfig.jwtIssuer,
            audience: _serverConfig.jwtAudience,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, id), new Claim(JwtRegisteredClaimNames.Name, name)],
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ClientConfig MakeConfig(string token)
        => new()
        {
            token = token,
            websocketServer = $"127.0.0.1:{_port}/ws",
            udpServer = ""
        };

    private TestClient CreateClient()
    {
        var c = new TestClient();
        _clients.Add(c);
        return c;
    }

    // ══════════════════════════════════════════════════════════
    // A  Connect
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task A1_client_connect_with_valid_token()
    {
        var tcs = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => tcs.TrySetResult(conn));

        var client = CreateClient();
        await client.ConnectServer(MakeConfig(CreateToken("user1", "Alice")));

        var conn = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        conn.Should().NotBeNull();
        conn.Id.Should().NotBeNullOrWhiteSpace();
        conn.User.Id.Should().Be("user1");
        conn.User.Name.Should().Be("Alice");
        conn.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task A2_client_connect_with_wrong_token()
    {
        var fired = false;
        _server.OnConnect(_ => fired = true);

        var client = CreateClient();
        var config = new ClientConfig
        {
            token = "bogus.invalid.token",
            websocketServer = $"127.0.0.1:{_port}/ws",
            udpServer = ""
        };

        var act = () => client.ConnectServer(config);
        await act.Should().ThrowAsync<Exception>();
        fired.Should().BeFalse();
    }

    [Fact]
    public async Task A3_client_connect_with_expired_token()
    {
        var fired = false;
        _server.OnConnect(_ => fired = true);

        var client = CreateClient();
        var act = () => client.ConnectServer(MakeConfig(CreateExpiredToken("u1", "A")));
        await act.Should().ThrowAsync<Exception>();
        fired.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════
    // B  Message
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task B1_client_sends_message_server_receives()
    {
        var connected = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => connected.TrySetResult(conn));

        var client = CreateClient();
        await client.ConnectServer(MakeConfig(CreateToken("u1", "A")));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<StringValue>();
        _server.SubscribeTcp<StringValue>("chat", (_, msg) => received.TrySetResult(msg));

        client.SendOnTcp("chat", new StringValue { Value = "hello" });

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Value.Should().Be("hello");
    }

    [Fact]
    public async Task B2_server_sends_message_client_receives()
    {
        var connected = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => connected.TrySetResult(conn));

        var client = CreateClient();
        await client.ConnectServer(MakeConfig(CreateToken("u1", "A")));
        var conn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<StringValue>();
        client.SubscribeTcp<StringValue>("echo", msg => received.TrySetResult(msg));

        _server.SendOnTcp("echo", conn, new StringValue { Value = "world" });

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Value.Should().Be("world");
    }

    [Fact]
    public async Task B3_two_clients_chat_through_server()
    {
        var conn1Tcs = new TaskCompletionSource<IConnection>();
        var conn2Tcs = new TaskCompletionSource<IConnection>();
        var connected = 0;
        _server.OnConnect(conn =>
        {
            var n = Interlocked.Increment(ref connected);
            if (n == 1) conn1Tcs.TrySetResult(conn);
            else conn2Tcs.TrySetResult(conn);
        });

        var client1 = CreateClient();
        var client2 = CreateClient();

        await client1.ConnectServer(MakeConfig(CreateToken("u1", "Alice")));
        _ = await conn1Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client2.ConnectServer(MakeConfig(CreateToken("u2", "Bob")));
        var conn2 = await conn2Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _server.SubscribeConnection(conn2.Id, "room");

        var client2Received = new TaskCompletionSource<StringValue>();
        client2.SubscribeTcp<StringValue>("room", msg => client2Received.TrySetResult(msg));

        var client1Echoed = false;
        client1.SubscribeTcp<StringValue>("room", _ => client1Echoed = true);

        client1.SendOnTcp("room", new StringValue { Value = "hi Bob" });

        var msg = await client2Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Value.Should().Be("hi Bob");
        client1Echoed.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════
    // C  Disconnect
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task C1_server_disconnect_fires_client_OnDisconnect()
    {
        var connected = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => connected.TrySetResult(conn));

        var client = CreateClient();
        await client.ConnectServer(MakeConfig(CreateToken("u1", "A")));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disconnected = new TaskCompletionSource();
        client.OnDisconnect(() => disconnected.TrySetResult());

        await client.CloseAsync(); // avoid client-side websocket holding server stop
        await _server.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task C2_client_disconnect_fires_server_OnDisconnect()
    {
        var connected = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => connected.TrySetResult(conn));

        var client = CreateClient();
        await client.ConnectServer(MakeConfig(CreateToken("u1", "A")));
        var conn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disconnected = new TaskCompletionSource<IConnection>();
        _server.OnDisconnect(dc => disconnected.TrySetResult(dc));

        await client.CloseAsync();

        var dc = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dc.Id.Should().Be(conn.Id);
    }

    [Fact]
    public async Task C3_reconnect_subscriptions_still_work()
    {
        var connTcs1 = new TaskCompletionSource<IConnection>();
        var connTcs2 = new TaskCompletionSource<IConnection>();
        var connects = 0;
        _server.OnConnect(conn =>
        {
            var n = Interlocked.Increment(ref connects);
            if (n == 1) connTcs1.TrySetResult(conn);
            else connTcs2.TrySetResult(conn);
        });

        var client = CreateClient();

        await client.ConnectServer(MakeConfig(CreateToken("u1", "A")));
        _ = await connTcs1.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<StringValue>();
        client.SubscribeTcp<StringValue>("echo", msg => received.TrySetResult(msg));

        await client.CloseAsync();

        await client.ConnectServer(MakeConfig(CreateToken("u1", "A")));
        var conn2 = await connTcs2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _server.SendOnTcp("echo", conn2, new StringValue { Value = "after-reconnect" });

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Value.Should().Be("after-reconnect");
    }
}

// ══════════════════════════════════════════════════════════════
//  TestClient —  ClientAbstract subclass using ClientWebSocket
// ══════════════════════════════════════════════════════════════

internal sealed class TestClient : ClientAbstract
{
    private ClientWebSocket? _ws;
    private readonly SubjectDispatcher _dispatcher = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<Action> _onDisconnectCallbacks = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _receiveCts;

    protected override async Task ConnectWebSocket(string token, string websocketServer)
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", "Bearer " + token);

        var uri = new Uri(websocketServer.Contains("://") ? websocketServer : "ws://" + websocketServer);
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _ws.ConnectAsync(uri, connectCts.Token);

        _receiveCts = new CancellationTokenSource();
        _ = ReceiveLoop();
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1024 * 64];
        try
        {
            while (_ws!.State == WebSocketState.Open)
            {
                if (_receiveCts!.IsCancellationRequested) break;

                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _receiveCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        goto fireDisconnect;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var envelope = MessageEnvelope.Parser.ParseFrom(ms.ToArray());
                _dispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }

        fireDisconnect:
        FireDisconnect();
    }

    private void FireDisconnect()
    {
        Action[] snapshot;
        lock (_gate) { snapshot = _onDisconnectCallbacks.ToArray(); }
        foreach (var cb in snapshot) cb();
    }

    protected override void NotifyConnectUdp(string token, string udpServer) { }
    protected override void AutoPingWebSocketAndUdpThread() { }

    public override ISubscribe OnDisconnect(Action onDisconnect)
    {
        lock (_gate) { _onDisconnectCallbacks.Add(onDisconnect); }
        return new Disposer(() => { lock (_gate) { _onDisconnectCallbacks.Remove(onDisconnect); } });
    }

    public override async void SendOnTcp<TData>(string subject, TData data)
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope
        {
            Subject = subject,
            Payload = ByteString.CopyFrom(payload)
        };
        await _sendLock.WaitAsync();
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.SendAsync(envelope.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override void SendOnUdp<TData>(string subject, TData data)
        => throw new NotImplementedException("UDP not implemented");

    public override ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data)
        => _dispatcher.Subscribe(subject, data);

    public override ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data)
        => throw new NotImplementedException("UDP not implemented");

    public async Task CloseAsync()
    {
        try
        {
            if (_ws?.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token);
            }
        }
        catch { }

        var cts = Interlocked.Exchange(ref _receiveCts, null);
        cts?.Cancel();
        cts?.Dispose();

        _ws?.Dispose();
    }

    private sealed class Disposer : ISubscribe
    {
        private readonly Action _action;
        public Disposer(Action action) => _action = action;
        public void UnSubscribe() => _action();
    }
}
