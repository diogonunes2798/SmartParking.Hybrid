using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache;

/// <summary>
/// Readiness check for Redis based on an async PING that honours the health-check timeout.
/// Replaces AspNetCore.HealthChecks.Redis, whose check ignores cancellation and therefore waits for the
/// client's own 5 s timeout when Redis is down (observed in Phase 1, see docs/DIARIO.md).
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Fast path: no round trip (and no wait) while the multiplexer knows it is disconnected.
        if (!connection.IsConnected)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Not connected.");
        }

        try
        {
            var latency = await connection.GetDatabase()
                .PingAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy($"PING {latency.TotalMilliseconds:F1} ms");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "PING timed out.");
        }
#pragma warning disable CA1031 // A health check must report any failure instead of throwing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new HealthCheckResult(context.Registration.FailureStatus, ex.Message, ex);
        }
    }
}
