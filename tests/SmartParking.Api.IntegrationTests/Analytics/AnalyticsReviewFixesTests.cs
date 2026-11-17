using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Application.Analytics;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Documents.OccupancyEvents;

namespace SmartParking.Api.IntegrationTests.Analytics;

/// <summary>
/// Fixes of the independent code review of Phase 6 (<c>docs/revisoes/fase-6-revisao-codigo.md</c>), each with the test
/// that would have caught the defect or the surviving mutation.
/// </summary>
[Collection(AnalyticsTestGroup.Name)]
public sealed class AnalyticsReviewFixesTests(AnalyticsFixture fixture)
{
    private const OccupancyEventType Occupied = OccupancyEventType.Occupied;
    private const OccupancyEventType Freed = OccupancyEventType.Freed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>
    /// Finding 2: the state of a spot does not depend on where the range starts. A car parked 30 hours before the window,
    /// with no event since, keeps the spot occupied whichever range is aggregated.
    /// </summary>
    [Fact]
    public async Task A_stay_longer_than_a_day_gives_the_same_rows_for_any_range()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        var from = Utc(11, 2, 12);
        await fixture.AppendAsync(facility, (spots[0], Occupied, from.AddHours(-30)));

        await fixture.AggregateAsync(from, from.AddHours(2));
        var narrow = await fixture.RowsAsync(facility);
        await fixture.AggregateAsync(from.AddHours(-6), from.AddHours(2));
        var wide = await fixture.RowsAsync(facility);

        Assert.Equal([3600L, 3600L], narrow.Select(r => r.OccupiedSeconds));
        Assert.Equal(
            narrow.Select(r => (r.HourStart, r.OccupiedSeconds)),
            wide.Where(r => r.HourStart >= from).Select(r => (r.HourStart, r.OccupiedSeconds)));
        Assert.All(wide, r => Assert.Equal(3600, r.OccupiedSeconds));
    }

    /// <summary>
    /// Mutation M3: two events of a spot with the same timestamp before the range; the one with the larger event id is
    /// the last (same order as the read model, ADR-0008), so the spot starts the range occupied.
    /// </summary>
    [Fact]
    public async Task A_tie_before_the_range_is_broken_by_the_larger_event_id()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        var from = Utc(11, 3, 12);
        var at = from.AddMinutes(-10);
        await fixture.AppendWithIdsAsync(
            facility,
            (new Guid("00000000-0000-0000-0000-0000000000f2"), spots[0], Occupied, at),
            (new Guid("00000000-0000-0000-0000-0000000000f1"), spots[0], Freed, at));

        await fixture.AggregateAsync(from, from.AddHours(1));

        Assert.Equal(3600, Assert.Single(await fixture.RowsAsync(facility)).OccupiedSeconds);
    }

    /// <summary>Mutation M2: milliseconds are rounded to the nearest second, half away from zero, not truncated.</summary>
    [Theory]
    [InlineData(400, 0)]
    [InlineData(500, 1)]
    [InlineData(1_500, 2)]
    [InlineData(1_499, 1)]
    [InlineData(3_600_000, 3_600)]
    public void Occupied_milliseconds_are_rounded_to_the_nearest_second(long milliseconds, long seconds) =>
        Assert.Equal(seconds, OccupancyAggregator.ToSeconds(milliseconds));

    [Fact]
    public async Task Sub_second_occupations_are_rounded_in_the_stored_row()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        var from = Utc(11, 4, 12);
        await fixture.AppendAsync(
            facility, (spots[0], Occupied, from.AddMilliseconds(600)), (spots[0], Freed, from.AddMilliseconds(2_100)));

        await fixture.AggregateAsync(from, from.AddHours(1));

        Assert.Equal(2, Assert.Single(await fixture.RowsAsync(facility)).OccupiedSeconds); // 1.5 s → 2 s
    }

    /// <summary>Mutation M1 and finding 11: the checkpoint never moves back and an older aggregation never overwrites a newer row.</summary>
    [Fact]
    public async Task The_store_keeps_the_newest_checkpoint_and_the_newest_row()
    {
        var facility = Guid.NewGuid();
        var hour = Utc(11, 5, 12);
        var newer = new OccupancyHourlySummary(facility, "store-test", hour, 2, 3_000, 7_200, 4, Utc(11, 5, 14));
        var older = newer with { OccupiedSeconds = 1_000, EventCount = 1, AggregatedAt = Utc(11, 5, 13) };

        await using var scope = fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOccupancySummaryStore>();
        var before = await store.GetCheckpointAsync(Ct);
        var high = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            Assert.Equal(high, await store.SaveAsync([newer], high, null, Ct));
            Assert.Equal(high, await store.SaveAsync([older], high.AddHours(-5), null, Ct));
            Assert.Equal(high, await store.GetCheckpointAsync(Ct));
        }
        finally
        {
            await ResetCheckpointAsync(before);
        }

        var row = Assert.Single(await fixture.RowsAsync(facility));
        Assert.Equal((3_000L, 4), (row.OccupiedSeconds, row.EventCount));
    }

    /// <summary>
    /// Finding 1: the state before a range is read with a DISTINCT_SCAN on the (facilityId, spotId, timestamp, eventId)
    /// index (about one index entry per spot), not by reading every event of the facility.
    /// </summary>
    [HybridFact]
    public async Task The_state_before_a_range_is_read_with_a_distinct_scan()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 2);
        var from = Utc(11, 6, 12);
        var events = Enumerable.Range(1, 50)
            .SelectMany(i => spots.Select(s => (s, i % 2 == 0 ? Freed : Occupied, from.AddMinutes(-i))))
            .ToArray();
        await fixture.AppendAsync(facility, events);
        var log = fixture.Services.GetRequiredService<MongoOccupancyEventLog>();
        await log.EnsureIndexesAsync(Ct);

        var states = await log.GetLatestPerSpotAsync(facility, from, Ct);
        var explain = await fixture.Services.GetRequiredService<IMongoDatabase>().RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "explain", new BsonDocument
                    {
                        { "aggregate", MongoOccupancyEventLog.CollectionName },
                        { "pipeline", new BsonArray(MongoOccupancyEventLog.LatestPerSpotPipeline(facility, from)) },
                        { "cursor", new BsonDocument() },
                    }
                },
                { "verbosity", "executionStats" },
            },
            cancellationToken: Ct);

        Assert.Equal(2, states.Count);
        Assert.All(states, s => Assert.Equal((Occupied, from.AddMinutes(-1)), (s.Type, s.Timestamp)));
        var plan = explain.ToJson();
        Assert.Contains("DISTINCT_SCAN", plan, StringComparison.Ordinal);
        Assert.Contains(MongoOccupancyEventLog.FacilitySpotTimestampIndexName, plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 6: the end of a week and of a month across a clock change comes from PostgreSQL, like the start.
    /// Week of Monday 2026-03-23 (WET) ends on Monday 2026-03-30 (WEST) = 167 hours; October 2026 has 745 hours.
    /// </summary>
    [Fact]
    public async Task Week_and_month_ends_follow_the_Lisbon_calendar()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        await fixture.AppendAsync(facility, (spots[0], Occupied, Utc(3, 25, 10)));
        await fixture.AggregateAsync(Utc(3, 25, 10), Utc(3, 25, 11));
        await fixture.AggregateAsync(Utc(10, 14, 10), Utc(10, 14, 11));

        var week = Assert.Single((await fixture.ReportAsync(zone, "2026-03-23T00:00:00Z", "2026-03-29T23:00:00Z", "week")).Buckets);
        var month = Assert.Single((await fixture.ReportAsync(zone, "2026-09-30T23:00:00Z", "2026-11-01T00:00:00Z", "month")).Buckets);

        Assert.Equal((Utc(3, 23, 0), Utc(3, 29, 23), 1, 167), (week.Start, week.End, week.Hours, week.ExpectedHours));
        Assert.Equal((Utc(9, 30, 23), Utc(11, 1, 0), 1, 745), (month.Start, month.End, month.Hours, month.ExpectedHours));
    }

    private async Task ResetCheckpointAsync(DateTimeOffset? value)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SmartParking.Infrastructure.Relational.SmartParkingDbContext>();
        if (value is { } position)
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync(
                context.Database, $"UPDATE analytics.aggregation_checkpoints SET position = {position}", Ct);
        }
        else
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync(
                context.Database, $"DELETE FROM analytics.aggregation_checkpoints", Ct);
        }
    }
}
