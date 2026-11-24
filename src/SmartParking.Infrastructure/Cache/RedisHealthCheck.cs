using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartParking.Infrastructure.Cache;

/// <summary>
/// Readiness check for Redis. Every wait observes the cancellation token, so the per-check
/// timeout (<see cref="HealthCheckTags.TimeoutKey"/>) bounds the response even when Redis
/// is down at start or stops while the host runs (spec 000, edge cases).
/// Failures propagate; the health check service reports them as Unhealthy.
/// </summary>
internal sealed class RedisHealthCheck(RedisConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var multiplexer = await connection.GetAsync(cancellationToken);
        await multiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken);

        return HealthCheckResult.Healthy();
    }
}
