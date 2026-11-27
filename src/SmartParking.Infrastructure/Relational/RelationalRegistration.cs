using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Infrastructure.Relational.Reading;
using SmartParking.Infrastructure.Relational.Repositories;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// PostgreSQL + PostGIS sublayer: one data source (with NetTopologySuite, RF-04), the DbContext as the
/// unit of work, and the readiness check. Registered the same way in both persistence modes (spec 001, AC-22).
/// The connection string is the application login (member of <c>smartparking_app</c>), never the owner.
/// </summary>
internal static class RelationalRegistration
{
    public const string ConnectionStringName = "Postgres";

    public const string HealthCheckName = "postgres";

    public static IServiceCollection AddRelational(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks,
        TimeSpan timeout)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.");

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseNetTopologySuite();
            return builder.Build();
        });

        services.AddDbContext<SmartParkingDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseNetTopologySuite()));
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<SmartParkingDbContext>());

        // Configuration ports (spec 001): one repository per aggregate root, one read port per query family.
        services.AddScoped<IParkingFacilityRepository, ParkingFacilityRepository>();
        services.AddScoped<IFacilityReader, FacilityReader>();
        services.AddScoped<IZoneRepository, ZoneRepository>();
        services.AddScoped<IZoneReader, ZoneReader>();
        services.AddScoped<IParkingSpotRepository, ParkingSpotRepository>();
        services.AddScoped<ISpotReader, SpotReader>();
        services.AddScoped<ISensorRepository, SensorRepository>();
        services.AddScoped<ISensorReader, SensorReader>();
        services.AddScoped<ITariffRepository, TariffRepository>();
        services.AddScoped<ITariffReader, TariffReader>();

        // Users, sessions and payments (spec 002), the same in both modes.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserReader, UserReader>();
        services.AddScoped<IParkingSessionRepository, ParkingSessionRepository>();
        services.AddScoped<ISessionReader, SessionReader>();
        services.AddScoped<IReservationRepository, ReservationRepository>();
        services.AddScoped<IReservationReader, ReservationReader>();

        // Ingestion registry check (spec 003): one compiled query per batch, the same in both modes.
        services.AddScoped<ISensorRegistryReader, SensorRegistryReader>();

        // Configuration read by the availability rebuild (spec 004); in both modes, like every relational port.
        services.AddScoped<IAvailabilityLayoutReader, AvailabilityLayoutReader>();

        healthChecks.AddNpgSql(
            sp => sp.GetRequiredService<NpgsqlDataSource>(),
            name: HealthCheckName,
            tags: [HealthCheckTags.Ready],
            timeout: timeout);

        return services;
    }
}
