using System.Runtime.CompilerServices;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;
using SmartParking.Application.Retention;

namespace SmartParking.Api.IntegrationTests.Retention;

/// <summary>
/// Steps of <see cref="OccupancyRetention"/> with in-memory stores (Phase 10, ADR-0013): an archive whose count does not
/// match the store is deleted and stops the run (mutation M6 of the diary); the guard fails closed when retention is off
/// and still runs when the archive throws (review of Phase 10, finding 1).
/// </summary>
public sealed class RetentionOrderTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_archive_with_fewer_events_than_the_store_is_deleted_and_nothing_is_archived_or_removed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "smartparking-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new RetentionOptions { RawEvents = TimeSpan.FromDays(1), ArchiveDelay = TimeSpan.Zero, ArchivePath = folder };
            var store = new FakeStore { Earliest = Day0.AddHours(10), Count = 3, Lines = ["{}", "{}"] }; // one event arrived meanwhile
            var ledger = new FakeLedger { CoveredHours = 24, AggregatedAt = Day0.AddDays(2) };
            var archive = new GzipNdjsonEventArchive(options);
            var retention = new OccupancyRetention(
                store, ledger, new FakeSnapshots(), new NoFacilities(), new EmptyLog(), archive, options, new FixedClock(Day0.AddDays(5)));

            var report = await retention.RunOnceAsync(Ct);

            Assert.Contains("the archive has 2 events, the store 3", report.Blocked, StringComparison.Ordinal);
            Assert.Equal(0, report.DaysArchived);
            Assert.Null(ledger.ArchivedUntil);
            Assert.Null(report.PurgeHorizon);
            Assert.Null(store.EnforcedHorizon);
            Assert.False(File.Exists(archive.PathOf(DateOnly.FromDateTime(Day0.UtcDateTime))));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Retention_switched_off_sets_the_store_back_to_never()
    {
        var store = new FakeStore();
        var options = new RetentionOptions { Enabled = false };
        var retention = new OccupancyRetention(
            store, new FakeLedger(), new FakeSnapshots(), new NoFacilities(), new EmptyLog(), new GzipNdjsonEventArchive(options), options,
            new FixedClock(Day0));

        var report = await retention.RunOnceAsync(Ct);

        Assert.Equal(1, store.Enforcements);
        Assert.Null(store.EnforcedHorizon);
        Assert.NotNull(report.Enforcement);
    }

    [Fact]
    public async Task A_failing_archive_still_refreshes_the_guard_with_the_current_horizon_and_rethrows()
    {
        var horizon = Day0.AddDays(-3);
        var store = new FakeStore { Earliest = Day0.AddHours(10), Count = 3 };
        var ledger = new FakeLedger { CoveredHours = 24, AggregatedAt = Day0.AddDays(2), ArchivedUntil = Day0 };
        await ledger.AdvancePurgeHorizonAsync(horizon, Ct);
        var options = new RetentionOptions { RawEvents = TimeSpan.FromDays(30), ArchiveDelay = TimeSpan.Zero };
        var retention = new OccupancyRetention(
            store, ledger, new FakeSnapshots(), new NoFacilities(), new EmptyLog(), new ThrowingArchive(), options,
            new FixedClock(Day0.AddDays(5)));

        await Assert.ThrowsAsync<IOException>(() => retention.RunOnceAsync(Ct));

        Assert.Equal(1, store.Enforcements);
        Assert.Equal(horizon, store.EnforcedHorizon);
    }

    [Fact]
    public async Task A_day_with_an_event_stored_after_its_aggregation_is_not_archived()
    {
        var store = new FakeStore { Earliest = Day0.AddHours(10), Count = 1, Lines = ["{}"], LastStored = Day0.AddDays(3) };
        var ledger = new FakeLedger { CoveredHours = 24, AggregatedAt = Day0.AddDays(2) };
        var options = new RetentionOptions { RawEvents = TimeSpan.FromDays(1), ArchiveDelay = TimeSpan.Zero, ArchivePath = Path.GetTempPath() };
        var retention = new OccupancyRetention(
            store, ledger, new FakeSnapshots(), new NoFacilities(), new EmptyLog(), new ThrowingArchive(), options,
            new FixedClock(Day0.AddDays(5)));

        var report = await retention.RunOnceAsync(Ct);

        Assert.Contains("stored after the last aggregation of 1 hour(s) (first 10:00)", report.Blocked, StringComparison.Ordinal);
        Assert.Null(ledger.ArchivedUntil);
    }

    private sealed class ThrowingArchive : IEventArchive
    {
        public Task<ArchiveFile> WriteDayAsync(DateOnly day, IAsyncEnumerable<string> lines, CancellationToken cancellationToken) =>
            throw new IOException("disk full");

        public void Delete(DateOnly day)
        {
        }
    }

    private sealed class FakeStore : IRawEventRetentionStore
    {
        public DateTimeOffset? Earliest { get; init; }

        public long Count { get; init; }

        public string[] Lines { get; init; } = [];

        public DateTimeOffset? EnforcedHorizon { get; private set; }

        public int Enforcements { get; private set; }

        /// <summary>storedAt of the events of the hour of <see cref="Earliest"/> (default: long before any aggregation).</summary>
        public DateTimeOffset LastStored { get; init; } = Day0.AddHours(11);

        public Task<IReadOnlyDictionary<DateTimeOffset, DateTimeOffset>> GetLastStoredAtPerHourAsync(
            DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<DateTimeOffset, DateTimeOffset>>(
                Earliest is { } e ? new Dictionary<DateTimeOffset, DateTimeOffset> { [OccupancyIntervalCalculator.FloorHour(e)] = LastStored } : []);

        public Task<DateTimeOffset?> GetEarliestTimestampAsync(CancellationToken cancellationToken) => Task.FromResult(Earliest);

        public Task<long> CountAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            Task.FromResult(Count);

        public async IAsyncEnumerable<string> ExportAsync(
            DateTimeOffset since, DateTimeOffset until, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var line in Lines)
            {
                await Task.Yield();
                yield return line;
            }
        }

        public Task<EnforcementResult> EnforceAsync(DateTimeOffset? horizon, CancellationToken cancellationToken)
        {
            EnforcedHorizon = horizon;
            Enforcements++;
            return Task.FromResult(new EnforcementResult(null, []));
        }
    }

    private sealed class FakeLedger : IRetentionLedger
    {
        public int CoveredHours { get; init; }

        public DateTimeOffset AggregatedAt { get; init; }

        public DateTimeOffset? ArchivedUntil { get; set; }

        public DateTimeOffset? Horizon { get; private set; }

        public Task<IReadOnlyDictionary<DateTimeOffset, DateTimeOffset>> GetAggregatedAtPerHourAsync(
            DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<DateTimeOffset, DateTimeOffset>>(
                Enumerable.Range(0, CoveredHours).ToDictionary(h => since.AddHours(h), _ => AggregatedAt));

        public Task<DateTimeOffset?> GetArchivedUntilAsync(CancellationToken cancellationToken) => Task.FromResult(ArchivedUntil);

        public Task RecordArchiveAsync(ArchivedDay day, CancellationToken cancellationToken)
        {
            ArchivedUntil = new DateTimeOffset(day.Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetPurgeHorizonAsync(CancellationToken cancellationToken) => Task.FromResult(Horizon);

        public Task<DateTimeOffset> AdvancePurgeHorizonAsync(DateTimeOffset horizon, CancellationToken cancellationToken)
        {
            Horizon = horizon;
            return Task.FromResult(horizon);
        }
    }

    private sealed class FakeSnapshots : ISpotStateSnapshotStore
    {
        public Task SaveAsync(IReadOnlyList<SpotSnapshot> entries, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SpotSnapshot>> GetAsync(Guid facilityId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotSnapshot>>([]);

        public Task<IReadOnlyList<SpotSnapshot>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotSnapshot>>([]);
    }

    private sealed class NoFacilities : IAnalyticsFacilitySource
    {
        public Task<IReadOnlyList<AnalyticsFacility>> GetFacilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AnalyticsFacility>>([]);
    }

    private sealed class EmptyLog : IOccupancyEventLog
    {
        public Task<AppendResult> AppendAsync(IReadOnlyList<OccupancyEventRecord> records, CancellationToken cancellationToken) =>
            Task.FromResult(AppendResult.Empty);

        public async IAsyncEnumerable<LatestSpotEvent> GetLatestPerSpotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<LatestSpotEvent>> GetLatestPerSpotAsync(Guid facilityId, DateTimeOffset before, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LatestSpotEvent>>([]);

        public Task<IReadOnlyList<SpotOccupancyEvent>> GetEventsAsync(
            Guid facilityId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotOccupancyEvent>>([]);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
