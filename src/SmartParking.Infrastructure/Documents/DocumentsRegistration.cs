using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SmartParking.Application.Abstractions.Documents;

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

        // Append-only occupancy event log (spec 003).
        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.SectionName))
            .Validate(options => options.IsValid(), MongoOptions.ValidationMessage)
            .ValidateOnStart();
        services.AddSingleton<OccupancyEventsCollection>();
        services.AddSingleton<IOccupancyEventLog, MongoOccupancyEventLog>();

        // Read side of the log for the availability rebuild, and its index (spec 004).
        services.AddSingleton<IOccupancyEventIndexes, OccupancyEventIndexes>();
        services.AddSingleton<ILatestOccupancyEventReader, LatestOccupancyEventReader>();
        services.AddHostedService<DocumentsInitializer>();

        healthChecks.AddMongoDb(
            sp => sp.GetRequiredService<IMongoClient>(),
            name: HealthCheckName,
            tags: [HealthCheckTags.Ready],
            timeout: timeout);

        return services;
    }
}
