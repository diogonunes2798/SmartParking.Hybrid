using SmartParking.Application.Configuration;

namespace SmartParking.Api;

/// <summary>
/// Registers the Application handlers used by this host. Done here, not in the Application project,
/// so that Application keeps no package dependencies at all (ADR-0001, ProjectGraphTests).
/// </summary>
internal static class ApplicationRegistration
{
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
        // RF-06 (configuration CRUD), command side. The query side is IConfigurationQueries (Relational).
        services.AddScoped<FacilityCommands>();
        services.AddScoped<SpotCommands>();
        services.AddScoped<SensorCommands>();
        services.AddScoped<TariffCommands>();
        return services;
    }
}
