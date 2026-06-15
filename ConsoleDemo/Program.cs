using Google.Protobuf.WellKnownTypes;
using MyConnection;

static void Header(string title)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine();
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('=', 60));
    Console.ResetColor();
    Console.WriteLine();
}

static void Info(string msg)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  i  {msg}");
    Console.ResetColor();
}

static void Ok(string msg)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  >  {msg}");
    Console.ResetColor();
}

// =================================================================
Header("MyConnection Echo Server");
Info("Server lang nghe WebSocket + UDP, echo lai moi message nhan duoc.");
Info("SubjectDispatcher DUNG CHUNG cho TCP va UDP.");
Info("  Subscribe 1 lan -> nhan message tu ca 2 transport.");
Info("");

var config = new ServerConfig
{
    websocketEndpoint = "127.0.0.1:9090/ws",
    udpPort = 9091,
    jwtSecret = "demo-secret-key-at-least-32-bytes!!",
    jwtIssuer = "demo-issuer",
    jwtAudience = "demo-audience"
};

var server = (ServerImplement)ServerImplement.Create(config);
var token = server.CreateToken("user1", "Demo User");

server.OnConnect(conn =>
{
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("+ CONNECT ");
    Console.ResetColor();
    Console.WriteLine($"{conn.User.Name} (Id={conn.Id})");
});

server.OnDisconnect(conn =>
{
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.Write("- DISCONNECT ");
    Console.ResetColor();
    Console.WriteLine($"{conn.User.Name} (Id={conn.Id})");
});

server.OnWarning(w =>
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write($"[{DateTime.Now:HH:mm:ss}] WARN ");
    Console.ResetColor();
    Console.Write($"[{w.Code}] ");
    Console.WriteLine(w.Message);
    if (w.Connection is not null)
        Console.WriteLine($"        Connection: {w.Connection.User.Name} (Id={w.Connection.Id})");
    if (w.Exception is not null)
        Console.WriteLine($"        Exception: {w.Exception.GetType().Name}: {w.Exception.Message}");
});

Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine($"  WS  : ws://127.0.0.1:{server.WebSocketPort}/ws");
Console.WriteLine($"  UDP : 127.0.0.1:{config.udpPort}");
Console.ResetColor();
Console.WriteLine();

// =================================================================
Header("JWT TOKEN (copy de ket noi)");
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"  {token}");
Console.ResetColor();
Console.WriteLine();

// =================================================================
Header("DANG KY HANDLER");

server.SubscribeTcp<StringValue>("echo", (conn, msg) =>
{
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"TCP nhan \"{msg.Value}\"");
    Console.ResetColor();
    Console.WriteLine($" tu {conn.User.Name}");
    server.SendOnTcp("echo", conn, new StringValue { Value = $"[TCP echo] {msg.Value}" });
});

server.SubscribeUdp<StringValue>("echo", (conn, msg) =>
{
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"UDP nhan \"{msg.Value}\"");
    Console.ResetColor();
    Console.WriteLine($" tu {conn.User.Name}");
    server.SendOnUdp("echo", conn, new StringValue { Value = $"[UDP echo] {msg.Value}" });
});

Ok("Subject \"echo\" da dang ky.");

// =================================================================
Header("HUONG DAN KET NOI TU UNITY CLIENT");

Info("Cach ket noi tu Unity (dung MyConnection package):");
Info("");
Info("  var clientConfig = new ClientConfig");
Info("  {");
Info("      token = \"<token o tren>\",");
Info("      websocketServer = $\"127.0.0.1:{server.WebSocketPort}/ws\",");
Info("      udpServer = $\"127.0.0.1:{config.udpPort}\"");
Info("  };");
Info("");
Info("  var client = IClient.Create();");
Info("  await client.ConnectServer(clientConfig);");
Info("");
Info("  client.SubscribeTcp<StringValue>(\"echo\", msg =>");
Info("  {");
Info("      Debug.Log($\"Echo: {msg.Value}\");");
Info("  });");
Info("");
Info("  client.SendOnTcp(\"echo\", new StringValue { Value = \"Hello!\" });");
Info("  client.SendOnUdp(\"echo\", new StringValue { Value = \"Hello UDP!\" });");
Info("");

// =================================================================
Header("LUONG HOAT DONG");

Info("TCP: Client WS send -> Server ReceiveLoop -> Route -> echo handler -> SendOnTcp -> WS send -> Client OnMessage");
Info("UDP: Client UdpClient send -> Server UdpListener.ReceiveLoop -> Route -> echo handler -> SendOnUdp -> UdpListener.SendTo");
Info("Ping: Client UdpPingService gui __ping__ -> Server ReceiveLoop reply __pong__ -> Client HandleUdpMessage -> OnPongReceived");
Info("Re-auth: Neu pong timeout -> Client OnUdpPingTimeout -> WS request_udp_auth -> key moi -> handshake lai");

// =================================================================
Header("CHO KET NOI (Ctrl+C de thoat)");
Console.WriteLine();

var tcs = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    tcs.TrySetResult();
};

await tcs.Task;

Console.WriteLine();
Header("SHUTDOWN");
Info("Dang dong server...");
await server.DisposeAsync();
Ok("Server da dung.");
Console.WriteLine();
