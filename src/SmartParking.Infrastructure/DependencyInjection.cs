using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Hosting;
using SmartParking.Infrastructure.Messaging;
using SmartParking.Infrastructure.Relational;
using SmartParking.Infrastructure.Payments;

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

        // Shared by both modes: the clock of every timestamp and the actor written to the audit trail (spec 001).
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentActor, AnonymousCurrentActor>();

        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .Validate(options => PersistenceOptions.TryParseMode(options.Mode, out _), PersistenceOptions.ValidationMessage)
            .ValidateOnStart();

        // Shared by both modes: the reservation rules, handed to the Application as a Domain policy (plan 002b, 5).
        services.AddOptions<ReservationOptions>()
            .Bind(configuration.GetSection(ReservationOptions.SectionName))
            .Validate(options => options.IsValid(), ReservationOptions.ValidationMessage)
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<ReservationOptions>>().Value.ToPolicy());

        // Shared by both modes: the ingestion rules (spec 003).
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .Validate(options => options.IsValid(), IngestionOptions.ValidationMessage)
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<IngestionOptions>>().Value.ToPolicy());

        // Shared by both modes: the availability queries' Retry-After (spec 004).
        services.AddOptions<AvailabilityOptions>()
            .Bind(configuration.GetSection(AvailabilityOptions.SectionName))
            .Validate(options => options.IsValid(), AvailabilityOptions.ValidationMessage)
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
            .AddMessaging()
            .AddPayments();

    /// <summary>Monolithic mode: PostgreSQL only. Extension point for the monolithic port implementations (F9).</summary>
    private static IServiceCollection AddMonolithicPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks,
        TimeSpan timeout) =>
        services
            .AddRelational(configuration, healthChecks, timeout)
            .AddSingleton<IOccupancyEventLog, MonolithicOccupancyEventLogPending>()
            .AddMonolithicAvailabilityPending()
            .AddMessaging()
            .AddPayments();

    private static IServiceCollection AddMonolithicAvailabilityPending(this IServiceCollection services)
    {
        services.AddSingleton<MonolithicAvailabilityPending>();
        services.AddSingleton<IOccupancyProjector>(provider => provider.GetRequiredService<MonolithicAvailabilityPending>());
        services.AddSingleton<IAvailabilityViewStore>(provider => provider.GetRequiredService<MonolithicAvailabilityPending>());
        services.AddSingleton<ILatestOccupancyEventReader>(provider => provider.GetRequiredService<MonolithicAvailabilityPending>());
        services.AddSingleton<IOccupancyEventIndexes>(provider => provider.GetRequiredService<MonolithicAvailabilityPending>());
        services.AddSingleton<IAvailabilityReader>(provider => provider.GetRequiredService<MonolithicAvailabilityPending>());
        return services;
    }
}
