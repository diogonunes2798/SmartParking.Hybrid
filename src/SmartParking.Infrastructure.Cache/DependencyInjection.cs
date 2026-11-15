using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache;

/// <summary>
/// Registers the in-memory key-value sub-layer (Redis). Phase 1: connection and readiness check only;
/// the availability read model (UC-03) arrives in Phase 4.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Key under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Redis";

    /// <summary>Name of the readiness health check.</summary>
    public const string HealthCheckName = "redis";

    public static IServiceCollection AddCacheStore(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<string> healthCheckTags)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{ConnectionStringName}'.");

        var options = ConfigurationOptions.Parse(connectionString);
        // Start even if Redis is down and keep reconnecting: readiness reports the outage
        // instead of the process crashing (needed for the RNF-03 failover scenario).
        options.AbortOnConnectFail = false;

        // Opened asynchronously (RedisCacheConnection): neither the host start nor the readiness probe waits for it.
        services.AddSingleton(_ => new RedisCacheConnection(options));
        services.AddHostedService<RedisConnectionWarmup>();

        services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>(
                HealthCheckName,
                failureStatus: null,
                tags: healthCheckTags,
                timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
