using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache;

/// <summary>
/// Single Redis multiplexer for the process, connected asynchronously on first use so that
/// no caller (in particular the readiness check) blocks a thread on the initial connect.
/// <c>AbortOnConnectFail=false</c> keeps reconnecting instead of failing for good.
/// </summary>
internal sealed class RedisConnection(string connectionString) : IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> multiplexer = new(() =>
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.ConnectAsync(options);
    });

    /// <summary>The connected multiplexer; the wait is abandoned when <paramref name="cancellationToken"/> fires.</summary>
    public async Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken) =>
        await multiplexer.Value.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (multiplexer.IsValueCreated && multiplexer.Value.IsCompletedSuccessfully)
        {
            var connected = await multiplexer.Value;
            await connected.DisposeAsync();
        }
    }
}
