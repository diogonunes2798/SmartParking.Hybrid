using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache;

/// <summary>
/// Opens the Redis connection while the host starts, instead of lazily on first resolution.
/// Otherwise the first resolution may happen when the readiness check is activated, outside its timeout,
/// and block a request for up to ConnectTimeout when Redis is down (Phase 1 review, see docs/DIARIO.md).
/// With AbortOnConnectFail = false the host still starts if Redis is down; the multiplexer keeps retrying.
/// </summary>
internal sealed class RedisConnectionWarmup(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.Run(() => services.GetRequiredService<IConnectionMultiplexer>(), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
