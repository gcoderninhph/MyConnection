#if NET9_0
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace MyConnection;

public static class MyConnectionWebApplicationExtensions
{
    public static WebApplication UseMyConnectionServer(this WebApplication app)
    {
        var config = app.Services.GetRequiredService<ServerKestrelConfig>();
        var server = (ServerKestrel)app.Services.GetRequiredService<IServer>();

        var hostServer = app.Services.GetRequiredService(
            typeof(Microsoft.AspNetCore.Hosting.Server.IServer)) as Microsoft.AspNetCore.Hosting.Server.IServer;
        var addresses = hostServer?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses != null)
        {
            addresses.Clear();
            addresses.Add(config.KestrelUrls);
        }

        server.ConfigureWebApplication(app);
        server.StartUdpAsync();
        return app;
    }
}
#endif
