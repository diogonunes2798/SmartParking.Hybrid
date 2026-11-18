using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Application.Ingestion;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// Registers the PostgreSQL implementations that replace MongoDB and Redis in the monolithic variant (spec §7, ADR-0012):
/// <see cref="IEventBus"/>, <see cref="IOccupancyEventLog"/> and <see cref="IAvailabilityQueries"/>. Call it after
/// <see cref="DependencyInjection.AddRelationalPersistence"/> and <em>instead of</em> the Documents, Cache and Messaging
/// registrations: nothing of the hybrid sub-layers is registered, so no Redis or MongoDB client exists in the process and
/// readiness checks PostgreSQL only.
/// </summary>
public static class MonolithicRegistration
{
    public static IServiceCollection AddMonolithicPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(MonolithicOptions.SectionName).Get<MonolithicOptions>() ?? new MonolithicOptions();
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.TryAddSingleton<OccupancyIngestionMetrics>();
        services.TryAddSingleton<ReadModelMetrics>();
        services.AddSingleton<EnvironmentalColumnsProbe>();
        services.AddSingleton<MonolithicOccupancyStore>();
        services.AddSingleton<IOccupancyEventLog>(sp => sp.GetRequiredService<MonolithicOccupancyStore>());
        services.AddSingleton<IEventBus, PostgresEventBus>();
        services.AddSingleton<IAvailabilityQueries, PostgresAvailabilityQueries>();
        services.AddSingleton<OccupancyPartitionMaintenance>();
        services.AddSingleton<MonolithicRawEventRetention>(); // retention by DROP of partitions (Phase 10, ADR-0013)
        services.AddSingleton<Application.Retention.IRawEventRetentionStore>(sp => sp.GetRequiredService<MonolithicRawEventRetention>());
        return services;
    }
}
