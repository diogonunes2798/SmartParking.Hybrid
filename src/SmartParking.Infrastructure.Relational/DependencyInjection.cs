using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Registers the relational sub-layer (PostgreSQL). Phase 1: connection and readiness check only;
/// EF Core, migrations and the outbox arrive in Phase 2.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Key under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>Name of the readiness health check.</summary>
    public const string HealthCheckName = "postgres";

    public static IServiceCollection AddRelationalPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<string> healthCheckTags)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{ConnectionStringName}'.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        services.AddHealthChecks()
            .AddNpgSql(name: HealthCheckName, tags: healthCheckTags, timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
