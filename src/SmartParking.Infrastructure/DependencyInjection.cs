using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Messaging;
using SmartParking.Infrastructure.Relational;

namespace SmartParking.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Validates <c>Persistence:Mode</c> on start and registers the connections and readiness checks
    /// of the selected mode. In F1 only the connections exist; F2 onwards adds the port implementations.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .Validate(options => PersistenceOptions.TryParseMode(options.Mode, out _), PersistenceOptions.ValidationMessage)
            .ValidateOnStart();

        if (!PersistenceOptions.TryParseMode(configuration[PersistenceOptions.ModeKey], out var mode))
        {
            // Nothing mode-specific to register: ValidateOnStart stops the host with a message naming the key.
            return services;
        }

        var healthChecks = services.AddHealthChecks();
        var timeout = configuration.GetValue(HealthCheckTags.TimeoutKey, HealthCheckTags.DefaultTimeout);

        return mode switch
        {
            PersistenceMode.Hybrid => services.AddHybridPersistence(configuration, healthChecks, timeout),
            PersistenceMode.Monolithic => services.AddMonolithicPersistence(configuration, healthChecks, timeout),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Hybrid mode: PostgreSQL, MongoDB and Redis. Extension point for the hybrid port implementations (F2+).</summary>
    private static IServiceCollection AddHybridPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks,
        TimeSpan timeout) =>
        services
            .AddRelational(configuration, healthChecks, timeout)
            .AddDocuments(configuration, healthChecks, timeout)
            .AddCache(configuration, healthChecks, timeout)
            .AddMessaging();

    /// <summary>Monolithic mode: PostgreSQL only. Extension point for the monolithic port implementations (F9).</summary>
    private static IServiceCollection AddMonolithicPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks,
        TimeSpan timeout) =>
        services
            .AddRelational(configuration, healthChecks, timeout)
            .AddMessaging();
}
