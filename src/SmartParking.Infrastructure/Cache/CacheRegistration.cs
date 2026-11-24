using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmartParking.Infrastructure.Cache;

/// <summary>Redis sublayer (availability state, GEO, pub/sub, Streams): connection and readiness check.</summary>
internal static class CacheRegistration
{
    public const string ConnectionStringName = "Redis";

    public const string HealthCheckName = "redis";

    public static IServiceCollection AddCache(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks,
        TimeSpan timeout)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.");

        services.AddSingleton(_ => new RedisConnection(connectionString));

        healthChecks.AddCheck<RedisHealthCheck>(
            HealthCheckName,
            tags: [HealthCheckTags.Ready],
            timeout: timeout);

        return services;
    }
}
