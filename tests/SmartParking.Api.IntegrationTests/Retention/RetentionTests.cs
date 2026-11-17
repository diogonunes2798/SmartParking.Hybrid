using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Retention;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Documents.OccupancyEvents;

namespace SmartParking.Api.IntegrationTests.Retention;

/// <summary>
/// Retention of the raw events (RNF-05, Phase 10, ADR-0013), in the variant under test: MongoDB TTL with the guard, or the
/// DROP of daily partitions. One scenario in order, because the purge horizon only moves forward:
/// <list type="number">
/// <item>nothing is archived or removed while a day is not aggregated (UC-04 reports would lose data);</item>
/// <item>an aggregated day is archived (file = store) and then removed, and nothing newer than the horizon is;</item>
/// <item>a manual aggregation before the horizon is refused (it would overwrite good rows from an incomplete log);</item>
/// <item>a car parked since a removed day still counts as occupied in a later hour (spot state snapshot), and the
/// «last event of every spot» used by the read model rebuild still has it.</item>
/// </list>
/// </summary>
[Collection(RetentionTestGroup.Name)]
public sealed class RetentionTests(RetentionFixture fixture)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_are_archived_and_removed_only_after_aggregation_and_parked_cars_survive_the_removal()
    {
        var a = OccupancyRetention.FloorDay(DateTimeOffset.UtcNow) - (Day * 20); // all three days older than RawEvents (10 d)
        var b = a + Day;
        var c = b + Day;
        var end = c + Day;
        var later = end + (Day * 2); // a later day with an event that is never aggregated: archiving stops there
        await fixture.PrepareDaysAsync(DateOnly.FromDateTime(a.UtcDateTime).AddDays(-1), 10);

        var (facility, spots) = await fixture.Analytics.CreateFacilityAsync(AnalyticsFixtureZone(), 2);
        var parked = spots[0];
        var busy = spots[1];
        await fixture.Analytics.AppendAsync(
            facility,
            (parked, OccupancyEventType.Occupied, a.AddHours(10)), // never freed: a long stay
            (busy, OccupancyEventType.Occupied, a.AddHours(10)),
            (busy, OccupancyEventType.Freed, a.AddHours(10.5)),
            (busy, OccupancyEventType.Occupied, b.AddHours(10)),
            (busy, OccupancyEventType.Freed, b.AddHours(10.5)),
            (busy, OccupancyEventType.Occupied, c.AddHours(10)),
            (busy, OccupancyEventType.Freed, c.AddHours(10.5)),
            (busy, OccupancyEventType.Occupied, later.AddHours(12)));
        Assert.Equal(7, await fixture.Store.CountAsync(a, end, Ct));

        // 1. Not aggregated: nothing archived, no horizon, nothing removed (the TTL index stays at «never»).
        var first = await RunRetentionAsync();
        Assert.Contains("0 of 24 hours aggregated", first.Blocked, StringComparison.Ordinal);
        Assert.Null(first.ArchivedUntil);
        Assert.Null(first.PurgeHorizon);
        if (TestMode.IsHybrid)
        {
            Assert.Equal(MongoOccupancyEventLog.NeverExpireSeconds, first.Enforcement!.ExpireAfterSeconds);
        }

        Assert.Empty(first.Enforcement!.DroppedPartitions);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // three TTL monitor passes
        Assert.Equal(7, await fixture.Store.CountAsync(a, end, Ct));

        // 2. Day A aggregated: archived (3 events), horizon at its end, A removed, B and C kept.
        await AggregateAsync(a, b);
        var second = await RunRetentionAsync();
        Assert.Equal((1, 3L), (second.DaysArchived, second.EventsArchived));
        Assert.Equal(b, second.ArchivedUntil);
        Assert.Equal(b, second.PurgeHorizon);
        Assert.Contains($"{b:yyyy-MM-dd} has 0 of 24 hours aggregated", second.Blocked, StringComparison.Ordinal);
        Assert.Equal(3, ReadArchive(DateOnly.FromDateTime(a.UtcDateTime)).Count);
        await EventuallyAsync(async () => await fixture.Store.CountAsync(a, b, Ct) == 0);
        Assert.Equal(4, await fixture.Store.CountAsync(b, end, Ct));

        // 3. A manual aggregation before the horizon is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => AggregateAsync(a, b));

        // 4. B and C aggregated: archived and removed. The two empty days after C are recorded without a file, and the
        // archive stops at the later day, not aggregated: horizon = min(now − 10 d, archived) = start of that day.
        await AggregateAsync(b, end);
        var third = await RunRetentionAsync();
        Assert.Equal((4, 4L), (third.DaysArchived, third.EventsArchived));
        Assert.Equal(later, third.PurgeHorizon);
        Assert.Contains($"{later:yyyy-MM-dd} has 0 of 24 hours aggregated", third.Blocked, StringComparison.Ordinal);
        await EventuallyAsync(async () => await fixture.Store.CountAsync(a, end, Ct) == 0);
        Assert.Equal(1, await fixture.Store.CountAsync(later, later + Day, Ct));

        // 5. Every event of both spots before the horizon is gone, yet the first hour after it counts the parked car as
        // occupied and the other spot as free (snapshot), and the latest events used by the read model rebuild still
        // have the parked car.
        var after = await AggregateAsync(later, later.AddHours(1));
        Assert.Equal(1, after.Rows);
        var row = (await fixture.Analytics.RowsAsync(facility)).Single(r => r.HourStart == later);
        Assert.Equal(3600, row.OccupiedSeconds); // parked: 3600 s; busy (FREED at C 10:30): 0 s
        var latest = new List<Application.Abstractions.LatestSpotEvent>();
        await foreach (var e in fixture.Log.GetLatestPerSpotAsync(Ct))
        {
            latest.Add(e);
        }

        Assert.Contains(latest, e => e.SpotId == parked && e.Type == OccupancyEventType.Occupied && e.Timestamp == a.AddHours(10));
    }

    private static string AnalyticsFixtureZone() => Analytics.AnalyticsFixture.NewZone();

    private async Task<RetentionReport> RunRetentionAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await fixture.Retention(scope.ServiceProvider).RunOnceAsync(Ct);
    }

    private async Task<Application.Analytics.AggregationReport> AggregateAsync(DateTimeOffset from, DateTimeOffset to)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await fixture.Aggregator(scope.ServiceProvider).AggregateAsync(from, to, Ct);
    }

    private List<string> ReadArchive(DateOnly day)
    {
        var path = new GzipNdjsonEventArchive(fixture.Options).PathOf(day);
        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>What the TTL monitor sees (hybrid): the index value and the server's TTL counters.</summary>
    private async Task<string> DiagnosticsAsync()
    {
        if (!TestMode.IsHybrid)
        {
            return string.Empty;
        }

        var client = fixture.Services.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var status = await client.GetDatabase("admin").RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument("serverStatus", 1), cancellationToken: Ct);
        var retention = fixture.Services.GetRequiredService<MongoRawEventRetention>();
        return $"expireAfterSeconds={await retention.CurrentExpireAfterSecondsAsync(Ct)}; ttl={status["metrics"]["ttl"]}; " +
               $"repl={status.Contains("repl")}";
    }

    /// <summary>The MongoDB TTL monitor removes asynchronously (every second here); the monolithic DROP is immediate.</summary>
    private async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The removal did not happen within 90 s. " + await DiagnosticsAsync());
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }
    }
}
