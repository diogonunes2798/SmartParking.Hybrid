using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Retention;
using SmartParking.Infrastructure.Documents.OccupancyEvents;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// Registers the document sub-layer (MongoDB): client, database, readiness check and the occupancy event log (D3).
/// </summary>
public static class DependencyInjection
{
    /// <summary>Key under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Mongo";

    /// <summary>Configuration key of the database name.</summary>
    public const string DatabaseNameKey = "Mongo:Database";

    /// <summary>Database used when <see cref="DatabaseNameKey"/> is not set.</summary>
    public const string DefaultDatabaseName = "smartparking";

    /// <summary>Name of the readiness health check.</summary>
    public const string HealthCheckName = "mongodb";

    public static IServiceCollection AddDocumentPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<string> healthCheckTags)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{ConnectionStringName}'.");
        var databaseName = configuration[DatabaseNameKey] is { Length: > 0 } name ? name : DefaultDatabaseName;

        // MongoClient is thread-safe and meant to be a singleton (driver guidance).
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        var logOptions = new MongoEventLogOptions { BlockCompressor = configuration[BlockCompressorKey] is { Length: > 0 } c ? c : null };
        logOptions.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(logOptions);
        services.AddSingleton<MongoOccupancyEventLog>();
        services.AddSingleton<IOccupancyEventLog>(sp => sp.GetRequiredService<MongoOccupancyEventLog>());

        // Retention (RNF-05, Phase 10, ADR-0013): the TTL guard, the export of a day and the oldest-event lookup.
        services.TryAddSingleton(_ => RetentionOptionsFrom(configuration));
        services.AddSingleton<MongoRawEventRetention>();
        services.AddSingleton<IRawEventRetentionStore>(sp => sp.GetRequiredService<MongoRawEventRetention>());

        services.AddHealthChecks()
            .AddMongoDb(name: HealthCheckName, tags: healthCheckTags, timeout: TimeSpan.FromSeconds(3));

        return services;
    }

    /// <summary>Configuration key of the block compressor of the events collection (Phase 10).</summary>
    public const string BlockCompressorKey = "Mongo:EventsBlockCompressor";

    /// <summary>The validated <c>Retention</c> section.</summary>
    public static RetentionOptions RetentionOptionsFrom(IConfiguration configuration)
    {
        var options = configuration.GetSection(RetentionOptions.SectionName).Get<RetentionOptions>() ?? new RetentionOptions();
        options.Validate();
        return options;
    }
}
