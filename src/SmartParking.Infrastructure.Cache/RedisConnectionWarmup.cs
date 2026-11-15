using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache;

/// <summary>
/// The read model's Redis connection, opened asynchronously. Phase 1 resolved a synchronous
/// <c>ConnectionMultiplexer.Connect</c> while the host started (so that the readiness check would not do it inside
/// its timeout); with an unreachable server that blocked the start of the host for about 8 s (review of Phase 3,
/// finding 2). Now the connection is a shared task: <see cref="RedisConnectionWarmup"/> starts it without waiting,
/// and <see cref="RedisHealthCheck"/> reports "connecting" until it completes, so neither the host nor a probe waits.
/// With AbortOnConnectFail = false the task completes even if Redis is down, and the multiplexer keeps reconnecting.
/// Phase 4 (read model) awaits <see cref="GetAsync"/>.
/// </summary>
internal sealed class RedisCacheConnection(ConfigurationOptions options) : IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> connection = new(
        () => Task.Run(async () => (IConnectionMultiplexer)await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false)));

    public Task<IConnectionMultiplexer> GetAsync() => connection.Value;

    public async ValueTask DisposeAsync()
    {
        if (connection.IsValueCreated && connection.Value.IsCompletedSuccessfully)
        {
            await connection.Value.Result.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Starts connecting while the host starts, without making the host wait for it.</summary>
internal sealed class RedisConnectionWarmup(RedisCacheConnection connection) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = connection.GetAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
