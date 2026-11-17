using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Api.IntegrationTests.Monolithic;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Retention;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Documents.OccupancyEvents;
using SmartParking.Infrastructure.Relational.Monolithic;

namespace SmartParking.Api.IntegrationTests.Retention;

/// <summary>Storage mechanisms of the retention (Phase 10, ADR-0013): the MongoDB collection and TTL index.</summary>
[Collection(RetentionTestGroup.Name)]
public sealed class MongoRetentionStorageTests(RetentionFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HybridFact]
    public async Task The_collection_gets_the_configured_compressor_and_the_ttl_index_starts_at_never_and_keeps_the_guard_value()
    {
        var database = fixture.Services.GetRequiredService<IMongoClient>().GetDatabase("compressor_" + Guid.NewGuid().ToString("N"));
        var log = new MongoOccupancyEventLog(database, TimeProvider.System, new MongoEventLogOptions { BlockCompressor = "zstd" });
        await log.EnsureIndexesAsync(Ct);

        var stats = await database.RunCommandAsync<BsonDocument>(new BsonDocument("collStats", MongoOccupancyEventLog.CollectionName), cancellationToken: Ct);
        Assert.Contains("block_compressor=zstd", stats["wiredTiger"]["creationString"].AsString, StringComparison.Ordinal);

        var retention = new MongoRawEventRetention(database, log, new RetentionOptions(), TimeProvider.System);
        Assert.Equal(MongoOccupancyEventLog.NeverExpireSeconds, await retention.CurrentExpireAfterSecondsAsync(Ct));

        // The guard lowers it; a process that starts later (new log instance) must not fail on the existing index.
        var enforced = await retention.EnforceAsync(DateTimeOffset.UtcNow.AddDays(-30), Ct);
        Assert.Equal(enforced.ExpireAfterSeconds, await retention.CurrentExpireAfterSecondsAsync(Ct));
        var restarted = new MongoOccupancyEventLog(database, TimeProvider.System, new MongoEventLogOptions());
        await restarted.EnsureIndexesAsync(Ct);
        Assert.Equal(enforced.ExpireAfterSeconds, await retention.CurrentExpireAfterSecondsAsync(Ct));
    }
}

/// <summary>Daily partitions of the monolithic variant (Phase 10, ADR-0013), in the monolithic fixture whatever the run mode.</summary>
[Collection(MonolithicTestGroup.Name)]
public sealed class MonolithicDayPartitionTests(MonolithicFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_monthly_partition_is_replaced_by_the_days_of_its_month_and_one_with_rows_is_kept()
    {
        // An empty month in the way: replaced by its 31 days, so that no day of January falls into DEFAULT.
        await fixture.ScalarAsync<int>($"SELECT {MonolithicSchema.EnsurePartitionsFunction}('2040-01-01'::date, 1)");
        Assert.Equal(31, await fixture.ScalarAsync<int>($"SELECT {MonolithicSchema.EnsureDayPartitionsFunction}('2040-01-15'::date, 1)"));
        Assert.False(await ExistsAsync("occupancy_events_y2040m01"));
        Assert.True(await ExistsAsync("occupancy_events_d20400101"));
        Assert.True(await ExistsAsync("occupancy_events_d20400131"));
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT {MonolithicSchema.EnsureDayPartitionsFunction}('2040-01-15'::date, 1)"));

        // A month with rows stays as it is (its events are never moved).
        await fixture.ScalarAsync<int>($"SELECT {MonolithicSchema.EnsurePartitionsFunction}('2041-01-01'::date, 1)");
        var store = fixture.Services.GetRequiredService<IOccupancyEventLog>();
        var at = new DateTimeOffset(2041, 1, 10, 8, 0, 0, TimeSpan.Zero);
        await store.AppendAsync([new OccupancyEventRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), OccupancyEventType.Occupied, at, "{}", at)], Ct);
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT {MonolithicSchema.EnsureDayPartitionsFunction}('2041-01-10'::date, 1)"));
        Assert.True(await ExistsAsync("occupancy_events_y2041m01"));
        Assert.False(await ExistsAsync("occupancy_events_d20410110"));

        // The DROP by horizon is tested in RetentionTests (own containers): here it would drop the partitions that the
        // other tests of this shared fixture use.
    }

    private async Task<bool> ExistsAsync(string table) =>
        await fixture.ScalarAsync<bool>("SELECT to_regclass(@name) IS NOT NULL", ("name", "monolith." + table));
}
