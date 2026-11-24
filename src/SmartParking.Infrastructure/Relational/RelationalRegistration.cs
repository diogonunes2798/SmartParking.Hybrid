using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace SmartParking.Infrastructure.Relational;

/// <summary>PostgreSQL + PostGIS sublayer: connection and readiness check. EF Core arrives in F2.</summary>
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

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        healthChecks.AddNpgSql(
            sp => sp.GetRequiredService<NpgsqlDataSource>(),
            name: HealthCheckName,
            tags: [HealthCheckTags.Ready],
            timeout: timeout);

        return services;
    }
}
