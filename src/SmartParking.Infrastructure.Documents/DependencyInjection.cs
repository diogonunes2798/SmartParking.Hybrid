using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// Registers the document sub-layer (MongoDB). Phase 1: client and readiness check only;
/// the occupancy event log (D3) arrives in Phase 3.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Key under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Mongo";

    /// <summary>Name of the readiness health check.</summary>
    public const string HealthCheckName = "mongodb";

    public static IServiceCollection AddDocumentPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<string> healthCheckTags)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{ConnectionStringName}'.");

        // MongoClient is thread-safe and meant to be a singleton (driver guidance).
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

        services.AddHealthChecks()
            .AddMongoDb(name: HealthCheckName, tags: healthCheckTags, timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
