using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

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

        // Created on first use; AbortOnConnectFail=false keeps retrying instead of failing the host.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        healthChecks.AddRedis(
            sp => sp.GetRequiredService<IConnectionMultiplexer>(),
            name: HealthCheckName,
            tags: [HealthCheckTags.Ready],
            timeout: timeout);

        return services;
    }
}
