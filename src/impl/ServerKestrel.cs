#if NET9_0
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Net.WebSockets;

namespace MyConnection;

public class ServerKestrel : ServerCore
{
    private WebApplication? _app;
    private bool _ownsApp;
    private readonly ServerKestrelConfig _kestrelConfig;

    internal ServerKestrel(ServerKestrelConfig config) : base(config)
    {
        _kestrelConfig = config;
    }

    public static IServer Create(ServerKestrelConfig config)
    {
        var server = new ServerKestrel(config);
        var builder = WebApplication.CreateBuilder(new string[0]);
        builder.WebHost.UseUrls(config.KestrelUrls);
        var app = builder.Build();
        server.ConfigureWebApplication(app);
        server._ownsApp = true;
        _ = server.StartTransportAsync(server._cts.Token);
        server.StartUdpAsync();
        return server;
    }

    public void ConfigureWebApplication(WebApplication app)
    {
        app.UseWebSockets();
        app.Map(_kestrelConfig.websocketEndpoint, HandleWebSocketAsync);
        app.MapPost(_kestrelConfig.restEndpoint, HandleRestAsync);
        _app = app;
    }

    public void StartUdpAsync()
    {
        _udpListener = new UdpListener(_sessionMap, _registry);
        _ = _udpListener.StartAsync(_config.udpPort, _cts.Token);
    }

    public void StopUdpAsync()
    {
        _udpListener?.StopAsync();
    }

    protected override async ValueTask StartTransportAsync(CancellationToken ct)
    {
        if (_ownsApp && _app != null)
        {
            await _app.StartAsync(ct);
            await _app.WaitForShutdownAsync(ct);
        }
    }

    private async Task HandleWebSocketAsync(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer "))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
        var token = auth["Bearer ".Length..].Trim();

        var principal = _tokenService.ValidateToken(token);
        if (principal is null)
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        var userId = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value ?? "";
        var userName = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name)?.Value ?? "";
        var user = new JwtUser(userId, userName);

        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await HandleWebSocketConnection(ws, user, _cts.Token);
    }

    private async Task HandleRestAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        ApiRequest apiRequest;
        try
        {
            apiRequest = ApiRequest.Parser.ParseFrom(body);
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var response = await HandleRestRequest(apiRequest);

        var responseBytes = response.ToByteArray();
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = responseBytes.Length;
        await ctx.Response.Body.WriteAsync(responseBytes);
    }

    protected override async ValueTask StopTransportAsync()
    {
        if (_ownsApp && _app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
#endif
