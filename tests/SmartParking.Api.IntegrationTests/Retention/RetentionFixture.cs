using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using SmartParking.Api.IntegrationTests.Analytics;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;
using SmartParking.Application.Retention;
using SmartParking.Infrastructure.Documents.OccupancyEvents;
using SmartParking.Infrastructure.Relational.Monolithic;

namespace SmartParking.Api.IntegrationTests.Retention;

/// <summary>
/// Phase 10 (RNF-05, ADR-0013): containers of their own (the purge horizon is global to a database and would refuse the
/// aggregations of the other analytics tests), in the variant of <see cref="TestMode.Current"/>. Retention is built as
/// in the Workers (<c>RetentionRegistration</c>), on the real clock: the MongoDB TTL monitor uses the server's clock.
/// The TTL monitor runs every second here instead of every minute.
/// </summary>
public sealed class RetentionFixture : IAsyncLifetime
{
    private readonly AnalyticsFixture _analytics = new();

    public AnalyticsFixture Analytics => _analytics;

    public IServiceProvider Services => _analytics.Services;

    public string ArchivePath { get; } = Path.Combine(Path.GetTempPath(), "smartparking-retention-" + Guid.NewGuid().ToString("N"));

    /// <summary>Settings of the tests: 10 days of raw events, archive as soon as a day is aggregated, 1 h of margin.</summary>
    public RetentionOptions Options => new()
    {
        RawEvents = TimeSpan.FromDays(10),
        ArchiveDelay = TimeSpan.Zero,
        GuardMargin = TimeSpan.FromHours(1),
        HorizonStep = TimeSpan.FromHours(1),
        ArchivePath = ArchivePath,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _analytics.InitializeAsync();
        if (TestMode.IsHybrid)
        {
            await Services.GetRequiredService<IMongoClient>().GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument { { "setParameter", 1 }, { "ttlMonitorSleepSecs", 1 } }, cancellationToken: Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _analytics.DisposeAsync();
        if (Directory.Exists(ArchivePath))
        {
            Directory.Delete(ArchivePath, recursive: true);
        }
    }

    /// <summary>The log as the Workers see it (snapshot-aware).</summary>
    public IOccupancyEventLog Log =>
        new SnapshotAwareOccupancyEventLog(Services.GetRequiredService<IOccupancyEventLog>(), Services.GetRequiredService<ISpotStateSnapshotStore>());

    /// <summary>
    /// The store with the settings of the tests: in the hybrid variant the guard margin comes from <see cref="Options"/>
    /// (the instance registered in the API has the default margin of 1 day, found in the first run of this test).
    /// </summary>
    public IRawEventRetentionStore Store => TestMode.IsHybrid
        ? new MongoRawEventRetention(
            Services.GetRequiredService<IMongoDatabase>(), Services.GetRequiredService<MongoOccupancyEventLog>(), Options, TimeProvider.System)
        : Services.GetRequiredService<IRawEventRetentionStore>();

    public OccupancyRetention Retention(IServiceProvider scoped) =>
        new(
            Store,
            scoped.GetRequiredService<IRetentionLedger>(),
            scoped.GetRequiredService<ISpotStateSnapshotStore>(),
            scoped.GetRequiredService<IAnalyticsFacilitySource>(),
            Log,
            new GzipNdjsonEventArchive(Options),
            Options,
            TimeProvider.System);

    public OccupancyAggregator Aggregator(IServiceProvider scoped) =>
        ActivatorUtilities.CreateInstance<OccupancyAggregator>(scoped, Log, new AnalyticsOptions(), _analytics.Metrics, TimeProvider.System);

    /// <summary>Monolithic: daily partitions for the days of the test (in the hybrid variant, nothing to do).</summary>
    public async Task PrepareDaysAsync(DateOnly from, int days)
    {
        if (TestMode.IsHybrid)
        {
            return;
        }

        var command = Services.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand($"SELECT {MonolithicSchema.EnsureDayPartitionsFunction}(@from_day, @days)");
        await using var _ = command;
        command.Parameters.AddWithValue("from_day", from);
        command.Parameters.AddWithValue("days", days);
        await command.ExecuteScalarAsync(Ct);
    }
}

[CollectionDefinition(Name)]
public sealed class RetentionTestGroup : ICollectionFixture<RetentionFixture>
{
    public const string Name = "Retention (own containers)";
}
