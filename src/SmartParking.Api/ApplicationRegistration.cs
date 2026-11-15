using SmartParking.Application.Configuration;
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
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
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
        return services;
    }
}
