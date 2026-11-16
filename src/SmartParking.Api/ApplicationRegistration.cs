using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartParking.Application.Analytics;
using SmartParking.Application.Configuration;
using SmartParking.Application.Ingestion;
using SmartParking.Application.Sessions;
using SmartParking.Application.Users;
using SmartParking.Domain.Tariffs;

namespace SmartParking.Api;

/// <summary>
/// Registers the Application handlers used by this host. Done here, not in the Application project,
/// so that Application keeps no package dependencies at all (ADR-0001, ProjectGraphTests).
/// </summary>
internal static class ApplicationRegistration
{
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services, IConfiguration configuration)
    {
        // UC-01 (ingestion, steps 1–2). The bus (IEventBus) is registered by Infrastructure.Messaging (hybrid) or by AddMonolithicPersistence (ADR-0012).
        services.AddSingleton(configuration.GetSection("Ingestion").Get<IngestionSettings>() ?? new IngestionSettings());
        services.AddSingleton<OccupancyIngestionMetrics>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IngestOccupancyBatchHandler>();

        // RF-06 (configuration CRUD), command side. The query side is IConfigurationQueries (Relational).
        services.AddScoped<FacilityCommands>();
        services.AddScoped<SpotCommands>();
        services.AddScoped<SensorCommands>();
        services.AddScoped<TariffCommands>();

        // UC-02 (sessions and payment) and its precondition (users and vehicles). The query side is
        // ISessionQueries (Relational). Pricing is provisional and replaceable (TODO(Diogo), ITariffPricing).
        services.AddScoped<SessionCommands>();
        services.AddScoped<UserCommands>();
        services.AddSingleton<ITariffPricing, ProvisionalTariffPricing>();

        // UC-04 (analytical report, RF-05), query side only: IOccupancyReportQueries (Relational) reads analytics.* alone.
        // The time zone is resolved now, so that an unknown name fails at startup (ADR-0010).
        var reports = configuration.GetSection(ReportSettings.SectionName).Get<ReportSettings>() ?? new ReportSettings();
        _ = reports.Resolve();
        services.AddSingleton(reports);
        services.AddScoped<OccupancyReportService>();
        return services;
    }
}
