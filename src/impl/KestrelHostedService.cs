#if NET9_0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MyConnection;

public static class MyConnectionServiceExtensions
{
    public static IServiceCollection AddMyConnectionServer(
        this IServiceCollection services,
        ServerKestrelConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton<IServer>(sp =>
        {
            var cfg = sp.GetRequiredService<ServerKestrelConfig>();
            return new ServerKestrel(cfg);
        });
        services.AddHostedService<KestrelHostedService>();
        return services;
    }

    public static IServiceCollection AddMyConnectionServer(
        this IServiceCollection services,
        Action<ServerKestrelConfig> configure)
    {
        var config = new ServerKestrelConfig();
        configure(config);
        return services.AddMyConnectionServer(config);
    }
}

internal class KestrelHostedService : IHostedService
{
    private readonly IServer _server;

    public KestrelHostedService(IServer server) => _server = server;

    public Task StartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _server.DisposeAsync();
}
#endif
