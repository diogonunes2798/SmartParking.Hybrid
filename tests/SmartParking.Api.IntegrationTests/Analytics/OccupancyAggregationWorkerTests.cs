using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Analytics;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Relational;

namespace SmartParking.Api.IntegrationTests.Analytics;

/// <summary>
/// The worker's run (ADR-0010): only closed hours, a checkpoint that moves forward, a backlog caught up over several runs,
/// no work while no new hour has closed, and a clock moving back. The only test that moves the checkpoint.
/// </summary>
[Collection(AnalyticsTestGroup.Name)]
public sealed class OccupancyAggregationWorkerTests(AnalyticsFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runs_aggregate_closed_hours_move_the_checkpoint_and_repeat_the_last_hours_idempotently()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        var ten = new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
        await fixture.AppendAsync(
            facility, (spots[0], OccupancyEventType.Occupied, ten), (spots[0], OccupancyEventType.Freed, ten.AddMinutes(30)));

        // 12:05 with 2 min of allowed lateness: the 11h hour is closed, the 12h hour is not.
        var clock = new ManualTimeProvider(ten.AddHours(2).AddMinutes(5));
        var options = new AnalyticsOptions(); // InitialHours 24, ReaggregateHours 2, MaxHoursPerRun 24

        await using var scope = fixture.Services.CreateAsyncScope();
        var aggregator = fixture.Aggregator(scope.ServiceProvider, clock, options);

        // First run: no checkpoint → starts 24 + 2 hours back and stops after 24 hours (backlog).
        var first = await aggregator.RunOnceAsync(Ct);
        Assert.Equal((ten.AddHours(-24), ten), (first.From, first.To));
        Assert.Equal(ten, await CheckpointAsync());

        // Second run: from the checkpoint minus 2 hours to the end of the last closed hour.
        var second = await aggregator.RunOnceAsync(Ct);
        Assert.Equal((ten.AddHours(-2), ten.AddHours(2)), (second.From, second.To));
        Assert.Equal(ten.AddHours(2), await CheckpointAsync());

        var rows = await fixture.RowsAsync(facility);
        Assert.Equal(1800, rows.Single(r => r.HourStart == ten).OccupiedSeconds);
        Assert.DoesNotContain(rows, r => r.HourStart >= ten.AddHours(2)); // 12h is not closed yet

        // Third run, same clock: no new hour has closed, so nothing is read (finding 1) and nothing changes.
        var third = await aggregator.RunOnceAsync(Ct);
        Assert.Equal(AggregationReport.Nothing, third);
        Assert.Equal(
            rows.Select(r => (r.HourStart, r.OccupiedSeconds, r.EventCount, r.AggregatedAt)),
            (await fixture.RowsAsync(facility)).Select(r => (r.HourStart, r.OccupiedSeconds, r.EventCount, r.AggregatedAt)));

        // A clock that moves back never aggregates backwards nor moves the checkpoint (mutation M4 of the review).
        clock.Now = ten.AddHours(-3);
        Assert.Equal(AggregationReport.Nothing, await aggregator.RunOnceAsync(Ct));
        Assert.Equal(ten.AddHours(2), await CheckpointAsync());

        // Before 13:02 the 12h hour stays open; after it, the run aggregates it plus the last 2 hours again (late events).
        clock.Now = ten.AddHours(3).AddMinutes(1);
        Assert.Equal(AggregationReport.Nothing, await aggregator.RunOnceAsync(Ct));
        Assert.DoesNotContain(await fixture.RowsAsync(facility), r => r.HourStart == ten.AddHours(2));
        clock.Now = ten.AddHours(3).AddMinutes(3);
        var fourth = await aggregator.RunOnceAsync(Ct);
        Assert.Equal((ten, ten.AddHours(3)), (fourth.From, fourth.To));
        Assert.Contains(await fixture.RowsAsync(facility), r => r.HourStart == ten.AddHours(2));
        Assert.Equal(ten.AddHours(3), await CheckpointAsync());
        Assert.Equal(1800, (await fixture.RowsAsync(facility)).Single(r => r.HourStart == ten).OccupiedSeconds);

        // A manual aggregation of an older range never moves the checkpoint back.
        await aggregator.AggregateAsync(ten.AddHours(-5), ten.AddHours(-4), Ct);
        Assert.Equal(ten.AddHours(3), await CheckpointAsync());

        // The manual command refuses hours that are not closed yet (finding 9): 13h closes at 14:02.
        await Assert.ThrowsAsync<ArgumentException>(() => aggregator.AggregateAsync(ten.AddHours(3), ten.AddHours(4), Ct));
    }

    private async Task<DateTimeOffset?> CheckpointAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        var position = await context.AggregationCheckpoints.AsNoTracking().Select(c => (DateTimeOffset?)c.Position).SingleOrDefaultAsync(Ct);
        return position?.ToUniversalTime();
    }
}
