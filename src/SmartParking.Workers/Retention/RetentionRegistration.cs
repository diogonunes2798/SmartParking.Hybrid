using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Retention;

namespace SmartParking.Workers.Retention;

/// <summary>Registration of the retention in the Workers (Phase 10, ADR-0013), for both variants.</summary>
internal static class RetentionRegistration
{
    /// <summary>
    /// Registers the retention services and wraps the registered <see cref="IOccupancyEventLog"/> in
    /// <see cref="SnapshotAwareOccupancyEventLog"/>, so that the aggregation and the read model rebuild keep the state of
    /// spots whose last event was removed. Call after the persistence registrations.
    /// </summary>
    public static IServiceCollection AddRetention(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RetentionOptions.SectionName).Get<RetentionOptions>() ?? new RetentionOptions();
        options.Validate();
        services.RemoveAll<RetentionOptions>();
        services.AddSingleton(options);
        services.AddSingleton<IEventArchive, GzipNdjsonEventArchive>();
        services.AddScoped<OccupancyRetention>();

        // Decorate the log: the registration of the persistence layer is replaced by a singleton decorator around it
        // (the log and the snapshot store are singletons; the bus consumers are singletons too).
        var inner = services.Last(d => d.ServiceType == typeof(IOccupancyEventLog));
        services.Remove(inner);
        services.AddSingleton<IOccupancyEventLog>(sp => new SnapshotAwareOccupancyEventLog(
            Resolve(sp, inner), sp.GetRequiredService<ISpotStateSnapshotStore>()));
        return services;
    }

    private static IOccupancyEventLog Resolve(IServiceProvider sp, ServiceDescriptor descriptor) =>
        descriptor switch
        {
            { ImplementationFactory: { } factory } => (IOccupancyEventLog)factory(sp),
            { ImplementationInstance: IOccupancyEventLog instance } => instance,
            { ImplementationType: { } type } => (IOccupancyEventLog)ActivatorUtilities.GetServiceOrCreateInstance(sp, type),
            _ => throw new InvalidOperationException("Unsupported registration of IOccupancyEventLog."),
        };
}
