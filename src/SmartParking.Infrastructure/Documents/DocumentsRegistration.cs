using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace SmartParking.Infrastructure.Documents;

/// <summary>MongoDB sublayer (append-only event log, from F4): client and readiness check.</summary>
internal static class DocumentsRegistration
{
    public const string ConnectionStringName = "Mongo";

    public const string HealthCheckName = "mongo";

    public static IServiceCollection AddDocuments(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks,
        TimeSpan timeout)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.");

        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

        healthChecks.AddMongoDb(
            sp => sp.GetRequiredService<IMongoClient>(),
            name: HealthCheckName,
            tags: [HealthCheckTags.Ready],
            timeout: timeout);

        return services;
    }
}
