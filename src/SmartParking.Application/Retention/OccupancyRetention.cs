using System.Globalization;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;

namespace SmartParking.Application.Retention;

/// <summary>Outcome of one retention run (logged by the Workers, checked by the tests and the benchmark).</summary>
/// <param name="Blocked">Why archiving stopped before reaching the end of the eligible days, or null.</param>
public sealed record RetentionReport(
    int DaysArchived,
    long EventsArchived,
    DateTimeOffset? ArchivedUntil,
    DateTimeOffset? PurgeHorizon,
    EnforcementResult? Enforcement,
    string? Blocked)
{
    public static RetentionReport Disabled { get; } = new(0, 0, null, null, null, "retention disabled");
}

/// <summary>
/// Retention of the raw occupancy events (RNF-05, Phase 10, ADR-0013), the same steps in both variants:
/// <list type="number">
/// <item><b>Archive.</b> The next UTC day after the archived position is written to a compressed file when (a) it ended at
/// least <see cref="RetentionOptions.ArchiveDelay"/> ago, (b) the UC-04 aggregation has covered its 24 hours, and (c) the
/// file has exactly as many events as the store. A day that fails (a) to (c) stops the archive for this run: the
/// position never skips a day, so nothing older than an unarchived day can be removed.</item>
/// <item><b>Snapshot.</b> Before the purge horizon moves, the last event of every spot before the new horizon is saved
/// (<see cref="ISpotStateSnapshotStore"/>), so that aggregations and read model rebuilds keep the state of spots whose
/// last event is removed.</item>
/// <item><b>Horizon.</b> The horizon is min(now − <see cref="RetentionOptions.RawEvents"/>, archived position): an event
/// is never removed before it is aggregated and archived. It only moves forward.</item>
/// <item><b>Enforce.</b> The store may remove what is older than the horizon (MongoDB TTL value, monolithic DROP).</item>
/// </list>
/// </summary>
public sealed class OccupancyRetention(
    IRawEventRetentionStore store,
    IRetentionLedger ledger,
    ISpotStateSnapshotStore snapshots,
    IAnalyticsFacilitySource facilities,
    IOccupancyEventLog log,
    IEventArchive archive,
    RetentionOptions options,
    TimeProvider time)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    public async Task<RetentionReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return RetentionReport.Disabled;
        }

        var now = time.GetUtcNow();
        var (days, events, blocked) = await ArchiveAsync(now, cancellationToken).ConfigureAwait(false);

        var archivedUntil = await ledger.GetArchivedUntilAsync(cancellationToken).ConfigureAwait(false);
        var horizon = await ledger.GetPurgeHorizonAsync(cancellationToken).ConfigureAwait(false);
        if (archivedUntil is { } archived)
        {
            var target = Min(now - options.RawEvents, archived);
            if (horizon is null || target >= horizon.Value + options.HorizonStep)
            {
                await SnapshotAsync(target, cancellationToken).ConfigureAwait(false);
                horizon = await ledger.AdvancePurgeHorizonAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }

        var enforcement = await store.EnforceAsync(horizon, cancellationToken).ConfigureAwait(false);
        return new RetentionReport(days, events, archivedUntil, horizon, enforcement, blocked);
    }

    private async Task<(int Days, long Events, string? Blocked)> ArchiveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        int days = 0;
        long events = 0;
        for (var i = 0; i < options.MaxDaysPerRun; i++)
        {
            var start = await ledger.GetArchivedUntilAsync(cancellationToken).ConfigureAwait(false);
            if (start is null)
            {
                var earliest = await store.GetEarliestTimestampAsync(cancellationToken).ConfigureAwait(false);
                if (earliest is null)
                {
                    return (days, events, null); // empty store: nothing to archive yet
                }

                start = FloorDay(earliest.Value);
            }

            var end = start.Value + Day;
            if (end > now - options.ArchiveDelay)
            {
                return (days, events, null); // the next day is not old enough: the normal stop
            }

            var day = DateOnly.FromDateTime(start.Value.UtcDateTime);
            var expected = await store.CountAsync(start.Value, end, cancellationToken).ConfigureAwait(false);
            if (expected == 0)
            {
                // A day without events: nothing to aggregate or keep. Recorded so the position moves on.
                await ledger.RecordArchiveAsync(new ArchivedDay(day, string.Empty, 0, 0, string.Empty, now), cancellationToken)
                    .ConfigureAwait(false);
                days++;
                continue;
            }

            var covered = await ledger.CountAggregatedHoursAsync(start.Value, end, cancellationToken).ConfigureAwait(false);
            if (covered < 24)
            {
                return (days, events, Invariant(
                    $"{day:yyyy-MM-dd} has {covered} of 24 hours aggregated (UC-04): run aggregate-occupancy for it first"));
            }

            var file = await archive.WriteDayAsync(day, store.ExportAsync(start.Value, end, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (file.Lines != expected)
            {
                archive.Delete(day);
                return (days, events, Invariant(
                    $"{day:yyyy-MM-dd}: the archive has {file.Lines} events, the store {expected}; retried on the next run"));
            }

            await ledger.RecordArchiveAsync(new ArchivedDay(day, file.Path, file.Lines, file.Bytes, file.Sha256, now), cancellationToken)
                .ConfigureAwait(false);
            days++;
            events += file.Lines;
        }

        return (days, events, null);
    }

    private async Task SnapshotAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        foreach (var facility in await facilities.GetFacilitiesAsync(cancellationToken).ConfigureAwait(false))
        {
            var latest = await log.GetLatestPerSpotAsync(facility.FacilityId, before, cancellationToken).ConfigureAwait(false);
            if (latest.Count > 0)
            {
                await snapshots.SaveAsync(latest.Select(e => new SpotSnapshot(facility.FacilityId, e)).ToList(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Start of the UTC day of <paramref name="instant"/>.</summary>
    public static DateTimeOffset FloorDay(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
