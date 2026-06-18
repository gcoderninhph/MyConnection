using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.Net.WebSockets;
using Google.Protobuf;

namespace MyConnection.Tests;

public class ConnectionTests : IAsyncLifetime
{
    private ServerConfig _serverConfig = null!;
    private ServerImplement _server = null!;
    private int _portTcp = 1125;
    private int _portUdp = 1126;
    private readonly List<IClient> _clients = new();

    public Task InitializeAsync()
    {
        _serverConfig = new ServerConfig
        {
            tcpPort = _portTcp,
            websocketEndpoint = "/ws",
            restEndpoint = "/api",
            restCompressedEnable = true,
            udpPort = _portUdp,
            jwtSecret = "test-secret-key-at-least-thirty-two-bytes-long!!",
            jwtIssuer = "test-issuer",
            jwtAudience = "test-audience"
        };
        _server = (ServerImplement)ServerImplement.Create(_serverConfig);

        _server.OnLogin<StringValue>(data =>
        {
            return Task.FromResult<IUser>(new TestUser($"id-{data.Value}", data.Value));
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients)
            await c.DisposeAsync();
        _clients.Clear();
        try { await _server.DisposeAsync(); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────

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

    private ClientConfig MakeConfig()
        => new()
        {
            tcpServer = $"127.0.0.1:{_portTcp}",
            restEndpoint = "/api",
            websocketEnpoint = "/ws",
            restCompressedEnable = true,
            udpServer = $"127.0.0.1:{_portUdp}"
        };

    private async Task<IClient> CreateAndConnect(string userName)
    {
        var client = IClient.Create(MakeConfig());
        _clients.Add(client);
        await client.Login(() => new StringValue { Value = userName });
        await client.ConnectServer();
        return client;
    }

    // ══════════════════════════════════════════════════════════
    // A  Connect
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task A1_client_connect_with_valid_token()
    {
        var tcs = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => tcs.TrySetResult(conn));

        var client = await CreateAndConnect("Alice");

        var conn = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        conn.Should().NotBeNull();
        conn.Id.Should().NotBeNullOrWhiteSpace();
        conn.User.Id.Should().Be("id-Alice");
        conn.User.Name.Should().Be("Alice");
        conn.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task A2_client_connect_with_wrong_token()
    {
        var fired = false;
        _server.OnConnect(_ => fired = true);

        var client = IClient.Create(MakeConfig());
        _clients.Add(client);
        ((ClientImplement)client).SetToken("bogus.invalid.token");

        var act = () => client.ConnectServer();
        await act.Should().ThrowAsync<Exception>();
        fired.Should().BeFalse();
    }

    [Fact]
    public async Task A3_client_connect_with_expired_token()
    {
        var fired = false;
        _server.OnConnect(_ => fired = true);

        var client = IClient.Create(MakeConfig());
        _clients.Add(client);
        ((ClientImplement)client).SetToken(CreateExpiredToken("u1", "A"));

        var act = () => client.ConnectServer();
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

        var client = await CreateAndConnect("A");
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

        var client = await CreateAndConnect("A");
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

        var client1 = await CreateAndConnect("Alice");
        _ = await conn1Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var client2 = await CreateAndConnect("Bob");
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

        var client = await CreateAndConnect("A");
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disconnected = new TaskCompletionSource();
        client.OnDisconnect(() => disconnected.TrySetResult());

        await client.DisconnectAsync();
        await _server.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task C2_client_disconnect_fires_server_OnDisconnect()
    {
        var connected = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => connected.TrySetResult(conn));

        var client = await CreateAndConnect("A");
        var conn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disconnected = new TaskCompletionSource<IConnection>();
        _server.OnDisconnect(dc => disconnected.TrySetResult(dc));

        await client.DisconnectAsync();

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

        var client = await CreateAndConnect("A");
        _ = await connTcs1.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<StringValue>();
        client.SubscribeTcp<StringValue>("echo", msg => received.TrySetResult(msg));

        await client.DisconnectAsync();

        await client.ConnectServer();
        var conn2 = await connTcs2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _server.SendOnTcp("echo", conn2, new StringValue { Value = "after-reconnect" });

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Value.Should().Be("after-reconnect");
    }

    // ══════════════════════════════════════════════════════════
    // D  OnWarning
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task D1_OnWarning_fires_W003_when_sending_TCP_before_connecting()
    {
        var client = IClient.Create(new ClientConfig());
        _clients.Add(client);
        var warning = new TaskCompletionSource<WarningInfo>();
        client.OnWarning(w => warning.TrySetResult(w));

        client.SendOnTcp("dummy", new StringValue { Value = "x" });

        var w = await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        w.Code.Should().Be("W003");
    }

    [Fact]
    public async Task D2_OnWarning_fires_W006_when_receiving_subject_with_no_subscriber()
    {
        var connected = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => connected.TrySetResult(conn));

        var client = await CreateAndConnect("A");
        var conn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var warning = new TaskCompletionSource<WarningInfo>();
        client.OnWarning(w => warning.TrySetResult(w));

        _server.SendOnTcp("no_sub", conn, new StringValue { Value = "x" });

        var w = await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        w.Code.Should().Be("W006");
        w.Message.Should().Contain("no_sub");
    }

    [Fact]
    public async Task D3_server_OnWarning_fires_W006_when_routing_to_subject_with_no_subscriber()
    {
        var connected = new TaskCompletionSource<IConnection>();
        _server.OnConnect(conn => connected.TrySetResult(conn));

        var client = await CreateAndConnect("A");
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var warning = new TaskCompletionSource<ServerWarningInfo>();
        _server.OnWarning(w =>
        {
            if (w.Code == "W006")
                warning.TrySetResult(w);
        });

        client.SendOnTcp("no_sub", new StringValue { Value = "x" });

        var w = await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        w.Code.Should().Be("W006");
        w.Message.Should().Contain("no_sub");
        w.Connection.Should().BeNull();
    }

    [Fact]
    public void D4_LatestRttMs_is_null_with_UDP_disabled()
    {
        var client = IClient.Create(MakeConfig());
        client.LatestRttMs.Should().BeNull();
    }
}

internal sealed record TestUser(string Id, string Name) : IUser;
