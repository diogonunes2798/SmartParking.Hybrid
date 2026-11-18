using System.Runtime.CompilerServices;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;
using SmartParking.Application.Retention;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Api.IntegrationTests.Retention;

/// <summary>
/// Merge rules of <see cref="SnapshotAwareOccupancyEventLog"/> (Phase 10, ADR-0013; test gap of the review of Phase 10):
/// the newer of log and snapshot wins, a snapshot entry at or after the requested instant is ignored, and a spot that is
/// only in the snapshot is returned by both readers.
/// </summary>
public sealed class SnapshotAwareLogTests
{
    private static readonly Guid Facility = Guid.NewGuid();
    private static readonly DateTimeOffset T = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_newer_event_wins_a_later_snapshot_is_ignored_and_a_snapshot_only_spot_is_kept()
    {
        Guid inBoth = Guid.NewGuid(), snapshotOnly = Guid.NewGuid(), snapshotTooLate = Guid.NewGuid();
        var logEvent = new LatestSpotEvent(Guid.NewGuid(), inBoth, OccupancyEventType.Freed, T.AddHours(-1));
        var olderSnapshot = new LatestSpotEvent(Guid.NewGuid(), inBoth, OccupancyEventType.Occupied, T.AddDays(-5));
        var parked = new LatestSpotEvent(Guid.NewGuid(), snapshotOnly, OccupancyEventType.Occupied, T.AddDays(-40));
        var late = new LatestSpotEvent(Guid.NewGuid(), snapshotTooLate, OccupancyEventType.Occupied, T);
        var log = new SnapshotAwareOccupancyEventLog(
            new FixedLog([logEvent]),
            new FixedSnapshots([new(Facility, olderSnapshot), new(Facility, parked), new(Facility, late)]));

        var before = (await log.GetLatestPerSpotAsync(Facility, T, Ct)).ToDictionary(e => e.SpotId);

        Assert.Equal(logEvent, before[inBoth]);                 // the log is newer than the snapshot
        Assert.Equal(parked, before[snapshotOnly]);             // only in the snapshot: its event was removed
        Assert.False(before.ContainsKey(snapshotTooLate));      // at the requested instant: not «before» it

        var all = new List<LatestSpotEvent>();
        await foreach (var e in log.GetLatestPerSpotAsync(Ct))
        {
            all.Add(e);
        }

        Assert.Contains(logEvent, all);
        Assert.Contains(parked, all);
        Assert.Contains(late, all);                             // the rebuild has no «before»: every snapshot entry counts
        Assert.DoesNotContain(olderSnapshot, all);
    }

    [Fact]
    public async Task A_snapshot_tie_on_the_timestamp_is_broken_by_the_event_id_like_the_read_model()
    {
        var spot = Guid.NewGuid();
        var low = new LatestSpotEvent(new Guid("00000000-0000-0000-0000-000000000001"), spot, OccupancyEventType.Freed, T.AddHours(-2));
        var high = new LatestSpotEvent(new Guid("00000000-0000-0000-0000-000000000002"), spot, OccupancyEventType.Occupied, T.AddHours(-2));
        var log = new SnapshotAwareOccupancyEventLog(new FixedLog([low]), new FixedSnapshots([new(Facility, high)]));

        Assert.Equal(high, Assert.Single(await log.GetLatestPerSpotAsync(Facility, T, Ct)));
    }

    private sealed class FixedLog(IReadOnlyList<LatestSpotEvent> latest) : IOccupancyEventLog
    {
        public Task<AppendResult> AppendAsync(IReadOnlyList<OccupancyEventRecord> records, CancellationToken cancellationToken) =>
            Task.FromResult(AppendResult.Empty);

        public async IAsyncEnumerable<LatestSpotEvent> GetLatestPerSpotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var e in latest)
            {
                await Task.Yield();
                yield return e;
            }
        }

        public Task<IReadOnlyList<LatestSpotEvent>> GetLatestPerSpotAsync(Guid facilityId, DateTimeOffset before, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LatestSpotEvent>>(latest.Where(e => e.Timestamp < before).ToList());

        public Task<IReadOnlyList<SpotOccupancyEvent>> GetEventsAsync(
            Guid facilityId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotOccupancyEvent>>([]);
    }

    private sealed class FixedSnapshots(IReadOnlyList<SpotSnapshot> entries) : ISpotStateSnapshotStore
    {
        public Task SaveAsync(IReadOnlyList<SpotSnapshot> entries1, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SpotSnapshot>> GetAsync(Guid facilityId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotSnapshot>>(entries.Where(e => e.FacilityId == facilityId).ToList());

        public Task<IReadOnlyList<SpotSnapshot>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(entries);
    }
}
