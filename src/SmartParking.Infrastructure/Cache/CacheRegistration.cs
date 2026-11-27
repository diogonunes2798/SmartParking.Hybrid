using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Infrastructure.Cache.Availability;

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

        // Availability view (spec 004). Registered in every hybrid host; nothing invokes the projector until F6.
        services.AddSingleton<IOccupancyProjector, OccupancyProjector>();
        services.AddSingleton<IAvailabilityViewStore, AvailabilityViewStore>();
        services.AddSingleton<IAvailabilityReader, AvailabilityReader>();

        healthChecks.AddCheck<RedisHealthCheck>(
            HealthCheckName,
            tags: [HealthCheckTags.Ready],
            timeout: timeout);

        return services;
    }
}
