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
Header("MyConnection Server Demo");
Info("Server lang nghe REST + WebSocket + UDP.");
Info("Mo phong day du: Login REST, GetRequest, PostRequest, echo WS/UDP.");
Info("");

var config = new ServerConfig
{
    tcpPort = 9090,
    websocketEndpoint = "/ws",
    restEndpoint = "/api",
    restCompressedEnable = true,
    udpPort = 9091,
    jwtSecret = "demo-secret-key-at-least-32-bytes!!",
    jwtIssuer = "demo-issuer",
    jwtAudience = "demo-audience"
};

var server = (ServerImplement)ServerImplement.Create(config);

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

// =================================================================
Header("ENDPOINTS");

Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine($"  TCP        : 127.0.0.1:{config.tcpPort}     (REST + WS chung port)");
Console.WriteLine($"  REST       : http://127.0.0.1:{config.tcpPort}{config.restEndpoint}");
Console.WriteLine($"  WebSocket  : ws://127.0.0.1:{config.tcpPort}{config.websocketEndpoint}");
Console.WriteLine($"  UDP        : 127.0.0.1:{config.udpPort}");
Console.WriteLine($"  Compress   : {(config.restCompressedEnable ? "bat (zip)" : "tat")}");
Console.ResetColor();
Console.WriteLine();

// =================================================================
Header("DANG KY HANDLER");

// --- OnLogin ---
server.OnLogin<StringValue>((loginData) =>
{
    var username = loginData.Value;
    if (string.IsNullOrWhiteSpace(username))
        throw new InvalidOperationException("Username khong duoc rong");

    Ok($"[Login] Nguoi dung \"{username}\" xac thuc thanh cong");
    return Task.FromResult<IUser>(new DemoUser(username, username));
});
Ok("OnLogin<StringValue> da dang ky (subject: __login__)");

// --- OnGetRequest ---
server.OnGetRequest<StringValue>("server_time", () =>
{
    var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    Info($"[GetRequest] server_time -> \"{now}\"");
    return Task.FromResult(new StringValue { Value = now });
});
Ok("OnGetRequest<StringValue>(\"server_time\") da dang ky");

// --- OnPostRequest ---
server.OnPostRequest<StringValue, StringValue>("echo_rest", (req) =>
{
    Info($"[PostRequest] echo_rest <- \"{req.Value}\"");
    var result = new StringValue { Value = $"[REST echo] {req.Value}" };
    Ok($"[PostRequest] echo_rest -> \"{result.Value}\"");
    return Task.FromResult(result);
});
Ok("OnPostRequest<StringValue,StringValue>(\"echo_rest\") da dang ky");

// --- Echo WS ---
server.SubscribeTcp<StringValue>("echo", (conn, msg) =>
{
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"TCP nhan \"{msg.Value}\"");
    Console.ResetColor();
    Console.WriteLine($" tu {conn.User.Name}");
    server.SendOnTcp("echo", conn, new StringValue { Value = $"[TCP echo] {msg.Value}" });
});
Ok("SubscribeTcp<StringValue>(\"echo\") da dang ky");

// --- Echo UDP ---
server.SubscribeUdp<StringValue>("echo", (conn, msg) =>
{
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"UDP nhan \"{msg.Value}\"");
    Console.ResetColor();
    Console.WriteLine($" tu {conn.User.Name}");
    server.SendOnUdp("echo", conn, new StringValue { Value = $"[UDP echo] {msg.Value}" });
});
Ok("SubscribeUdp<StringValue>(\"echo\") da dang ky");

Console.WriteLine();

// =================================================================
Header("HUONG DAN KET NOI TU UNITY CLIENT");

Info("1. Tao client voi config:");
Info("");
Info("   var clientConfig = new ClientConfig");
Info("   {");
Info("       tcpServer = \"127.0.0.1:9090\",");
Info("       websocketEnpoint = \"/ws\",");
Info("       restEndpoint = \"/api\",");
Info("       restCompressedEnable = true,");
Info("       udpServer = \"127.0.0.1:9091\"");
Info("   };");
Info("   var client = IClient.Create(clientConfig);");
Info("");
Info("2. Dang nhap de lay token:");
Info("");
Info("   await client.Login(() => new StringValue { Value = \"player1\" });");
Info("");
Info("3. Ket noi WebSocket + UDP:");
Info("");
Info("   await client.ConnectServer();");
Info("");
Info("4. Goi REST API:");
Info("");
Info("   var time = await client.GetRequest<StringValue>(\"server_time\");");
Info("   Debug.Log($\"Server time: {time.Value}\");");
Info("");
Info("   var echo = await client.PostRequest<StringValue, StringValue>(");
Info("       \"echo_rest\", new StringValue { Value = \"Hello!\" });");
Info("   Debug.Log($\"Echo: {echo.Value}\");");
Info("");
Info("5. Goi real-time qua WebSocket/UDP:");
Info("");
Info("   client.SendOnTcp(\"echo\", new StringValue { Value = \"Hello TCP!\" });");
Info("   client.SendOnUdp(\"echo\", new StringValue { Value = \"Hello UDP!\" });");
Info("");

// =================================================================
Header("TEST REST API BANG curl");

Info("Luu y: payload la protobuf binary, can serialize truoc bang protoc.");
Info("Duoi day la vi du conceptual:");
Info("");
Info("  # 1. Login de lay token");
Info("  curl -X POST http://127.0.0.1:9090/api \\");
Info("    -H \"Content-Type: application/octet-stream\" \\");
Info("    --data-binary @login_request.bin");
Info("");
Info("  # ApiRequest (proto): subject=__login__, has_payload=true,");
Info("  #   payload=StringValue{value=\"alice\"}");
Info("");
Info("  # 2. Get server_time (kem token tu Login)");
Info("  curl -X POST http://127.0.0.1:9090/api \\");
Info("    -H \"Content-Type: application/octet-stream\" \\");
Info("    --data-binary @get_time_request.bin");
Info("");
Info("  # ApiRequest (proto): subject=server_time, has_payload=false,");
Info("  #   token=<token tu login>");
Info("");
Info("  # 3. Post echo_rest (kem token + payload)");
Info("  curl -X POST http://127.0.0.1:9090/api \\");
Info("    -H \"Content-Type: application/octet-stream\" \\");
Info("    --data-binary @echo_request.bin");
Info("");
Info("  # ApiRequest (proto): subject=echo_rest, has_payload=true,");
Info("  #   payload=StringValue{value=\"hello\"}, token=<token>");
Info("");

// =================================================================
Header("LUONG HOAT DONG");

Info("REST:  Client POST ApiRequest -> Server HandleRestRequest -> dispatch -> tra ApiResponse");
Info("TCP:   Client WS send -> Server ReceiveLoop -> Route -> echo handler -> SendOnTcp -> WS send -> Client OnMessage");
Info("UDP:   Client UdpClient send -> Server UdpListener.ReceiveLoop -> Route -> echo handler -> SendOnUdp -> UdpListener.SendTo");
Info("Ping:  Client UdpPingService gui __ping__ -> Server ReceiveLoop reply __pong__ -> Client HandleUdpMessage -> OnPongReceived");
Info("Login: Client Login(data) -> REST POST /api (subject=__login__) -> Server OnLogin handler -> LoginResponse(token) -> client luu token");
Info("Get:   Client GetRequest(subj) -> REST POST /api (subject=subj,has_payload=false,token) -> Server OnGetRequest handler -> response");
Info("Post:  Client PostRequest(subj,body) -> REST POST /api (subject=subj,has_payload=true,token) -> Server OnPostRequest handler -> response");

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

internal record DemoUser(string Id, string Name) : IUser;

