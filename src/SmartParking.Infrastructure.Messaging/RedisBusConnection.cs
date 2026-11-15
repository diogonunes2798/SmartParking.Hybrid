using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Messaging;

/// <summary>
/// The bus's own Redis connection, opened asynchronously (review of Phase 3, finding 2).
/// <see cref="ConnectionMultiplexer.Connect(ConfigurationOptions, TextWriter?)"/> is synchronous and, with an
/// unreachable server, blocks for about ConnectTimeout + 3 s (8.2 s measured); resolving it while the host starts
/// delayed /health/live of the Workers by 8.8 s. Here the connection is a shared task started by
/// <see cref="RedisBusConnectionWarmup"/> and awaited only by the code that sends commands.
/// It is separate from the read-model connection of Infrastructure.Cache: the sub-layers do not share code
/// (ADR-0001), and the consumer's polling does not queue behind the read model's commands.
/// With AbortOnConnectFail = false the task completes even if Redis is down, and the multiplexer keeps reconnecting.
/// </summary>
internal sealed class RedisBusConnection(ConfigurationOptions options) : IAsyncDisposable
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
internal sealed class RedisBusConnectionWarmup(RedisBusConnection connection) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = connection.GetAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
