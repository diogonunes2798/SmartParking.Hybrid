using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Configuration;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Tariffs;
using SmartParking.Infrastructure.Relational.Auditing;
using SmartParking.Infrastructure.Relational.Persistence;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Registers the relational sub-layer (PostgreSQL): one Npgsql data source shared by EF Core and the
/// readiness check, the DbContext with the audit interceptor, repositories (including D1's session
/// repository), the unit of work and the RF-06 queries.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Key under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>Name of the readiness health check.</summary>
    public const string HealthCheckName = "postgres";

    /// <summary>Name of the EF Core migrations history table (snake_case, like the rest of the schema).</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>Actor recorded in the audit log until authentication/RBAC exists (RNF-06).</summary>
    public const string AnonymousActor = "anonymous";

    public static IServiceCollection AddRelationalPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<string> healthCheckTags)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{ConnectionStringName}'.");

        // A single connection pool for EF Core and the health check (Phase 1 review note).
        services.AddNpgsqlDataSource(connectionString);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentActor>(new FixedActor(AnonymousActor));
        services.AddSingleton<AuditSaveChangesInterceptor>();

        services.AddDbContext<SmartParkingDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql
                .MigrationsHistoryTable(MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IRepository<ParkingFacility>, EfRepository<ParkingFacility>>();
        services.AddScoped<IRepository<ParkingSpot>, EfRepository<ParkingSpot>>();
        services.AddScoped<IRepository<Sensor>, EfRepository<Sensor>>();
        services.AddScoped<IRepository<Tariff>, EfRepository<Tariff>>();
        services.AddScoped<IParkingSessionRepository, EfParkingSessionRepository>();
        services.AddScoped<IConfigurationQueries, EfConfigurationQueries>();

        services.AddHealthChecks()
            .AddNpgSql(name: HealthCheckName, tags: healthCheckTags, timeout: TimeSpan.FromSeconds(3));

        return services;
    }

    private sealed record FixedActor(string Name) : ICurrentActor;
}
