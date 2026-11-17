using System.Diagnostics;
using System.Globalization;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Diagnostics;
using SmartParking.Application.Retention;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Analytics;

/// <summary>Counts of one aggregation (logged, and checked by the tests and the benchmarks).</summary>
/// <param name="From">Start of the range (inclusive), or null when there was nothing to aggregate.</param>
/// <param name="To">End of the range (exclusive).</param>
public sealed record AggregationReport(DateTimeOffset? From, DateTimeOffset? To, int Facilities, int Hours, int Rows, int Events, TimeSpan Duration)
{
    public static AggregationReport Nothing { get; } = new(null, null, 0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Aggregates the immutable occupancy log (MongoDB, D3) into the hourly summary table (PostgreSQL, D4 default) for UC-04
/// and RF-05 (ADR-0010). Only closed hours are aggregated; each hour is written with an upsert, so aggregating a
/// window again (a late event, a second Workers instance, a manual backfill) gives the same rows.
/// <para>
/// Reads the operational configuration (facilities, zones, spots) and the event log; the report, on the other side,
/// reads only the summary table (spec §4).
/// </para>
/// </summary>
public sealed class OccupancyAggregator(
    IAnalyticsFacilitySource facilities,
    IOccupancyEventLog log,
    IOccupancySummaryStore store,
    IRetentionLedger retention,
    AnalyticsOptions options,
    AnalyticsMetrics metrics,
    TimeProvider time)
{
    /// <summary>End (exclusive) of the last closed hour at <paramref name="now"/>.</summary>
    public DateTimeOffset ClosedUntil(DateTimeOffset now) => OccupancyIntervalCalculator.FloorHour(now - options.AllowedLateness);

    /// <summary>
    /// One run of the worker. Nothing is read while no new hour has closed since the checkpoint (review of Phase 6,
    /// finding 1: re-reading the last hours every minute would compete with the ingestion in MongoDB). When one has, the
    /// run aggregates the new hours plus the last <see cref="AnalyticsOptions.ReaggregateHours"/> again (late events),
    /// at most <see cref="AnalyticsOptions.MaxHoursPerRun"/>, and moves the checkpoint.
    /// </summary>
    public async Task<AggregationReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        var closedUntil = ClosedUntil(time.GetUtcNow());
        var checkpoint = await store.GetCheckpointAsync(cancellationToken).ConfigureAwait(false);
        if (checkpoint >= closedUntil)
        {
            metrics.Checkpoint(checkpoint); // also when the clock moved back: never aggregate backwards
            return AggregationReport.Nothing;
        }

        var resumeAt = checkpoint ?? closedUntil - (OccupancyIntervalCalculator.Hour * options.InitialHours);
        var from = resumeAt - (OccupancyIntervalCalculator.Hour * options.ReaggregateHours);
        var maxEnd = from + (OccupancyIntervalCalculator.Hour * options.MaxHoursPerRun);
        var to = closedUntil < maxEnd ? closedUntil : maxEnd; // long backlog: catch up over several runs

        // Never read hours whose events retention may have removed (Phase 10, ADR-0013): they would be aggregated again
        // from an incomplete log and overwrite good rows. Normally far behind (30 days vs. a few hours).
        if (await retention.GetPurgeHorizonAsync(cancellationToken).ConfigureAwait(false) is { } horizon
            && from < OccupancyIntervalCalculator.CeilHour(horizon))
        {
            from = OccupancyIntervalCalculator.CeilHour(horizon);
            if (from >= to)
            {
                metrics.Checkpoint(await store.SaveAsync([], to, null, cancellationToken).ConfigureAwait(false));
                return AggregationReport.Nothing;
            }
        }

        return await AggregateSliceAsync(from, to, checkpoint: to, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Aggregates every hour of [<paramref name="from"/>, <paramref name="to"/>) for every facility and upserts the rows
    /// (the <c>aggregate-occupancy</c> command, backfills and tests). The range is split into slices of
    /// <see cref="AnalyticsOptions.MaxHoursPerRun"/> hours, each read and saved on its own, so memory stays bounded
    /// (review of Phase 6, finding 3). Only closed hours are accepted (finding 9). The worker's checkpoint is not touched.
    /// </summary>
    public async Task<AggregationReport> AggregateAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        EnsureRange(from, to);
        var closedUntil = ClosedUntil(time.GetUtcNow());
        if (to > closedUntil)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The range must end at a closed hour (at most {closedUntil:O})."), nameof(to));
        }

        if (await retention.GetPurgeHorizonAsync(cancellationToken).ConfigureAwait(false) is { } horizon && from < horizon)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture,
                    $"The range starts before the retention purge horizon ({horizon:O}): those events may already be removed (ADR-0013)."),
                nameof(from));
        }

        var stopwatch = Stopwatch.StartNew();
        var slice = OccupancyIntervalCalculator.Hour * options.MaxHoursPerRun;
        int facilityCount = 0, rows = 0, events = 0;
        for (var start = from; start < to; start += slice)
        {
            var end = start + slice < to ? start + slice : to;
            var part = await AggregateSliceAsync(start, end, checkpoint: null, cancellationToken).ConfigureAwait(false);
            facilityCount = part.Facilities;
            rows += part.Rows;
            events += part.Events;
        }

        return new AggregationReport(from, to, facilityCount, Hours(from, to), rows, events, stopwatch.Elapsed);
    }

    private async Task<AggregationReport> AggregateSliceAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset? checkpoint, CancellationToken cancellationToken)
    {
        EnsureRange(from, to);
        using var activity = SmartParkingTelemetry.ActivitySource.StartActivity("analytics aggregate");
        activity?.SetTag("smartparking.analytics.from", from.ToString("O", CultureInfo.InvariantCulture));
        activity?.SetTag("smartparking.analytics.to", to.ToString("O", CultureInfo.InvariantCulture));

        var stopwatch = Stopwatch.StartNew();

        // Taken before reading: a row is only replaced by one aggregated later (finding 11).
        var aggregatedAt = time.GetUtcNow();
        var snapshot = await facilities.GetFacilitiesAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<OccupancyHourlySummary>(snapshot.Count * Hours(from, to));
        var events = 0;

        foreach (var facility in snapshot)
        {
            var initial = await log.GetLatestPerSpotAsync(facility.FacilityId, from, cancellationToken).ConfigureAwait(false);
            var states = new Dictionary<Guid, OccupancyEventType>(initial.Count);
            foreach (var last in initial)
            {
                states[last.SpotId] = last.Type;
            }

            var inRange = await log.GetEventsAsync(facility.FacilityId, from, to, cancellationToken).ConfigureAwait(false);
            events += inRange.Count;

            var spotCount = facility.SpotIds.Count;
            foreach (var hour in OccupancyIntervalCalculator.Compute(from, to, facility.SpotIds, states, inRange))
            {
                rows.Add(new OccupancyHourlySummary(
                    facility.FacilityId,
                    facility.Zone,
                    hour.HourStart,
                    spotCount,
                    ToSeconds(hour.OccupiedMilliseconds),
                    spotCount * 3600L,
                    hour.EventCount,
                    aggregatedAt));
            }
        }

        var stored = await store.SaveAsync(rows, checkpoint, new AggregatedRange(from, to), cancellationToken).ConfigureAwait(false);

        var report = new AggregationReport(from, to, snapshot.Count, Hours(from, to), rows.Count, events, stopwatch.Elapsed);
        metrics.Aggregated(report);
        metrics.Checkpoint(stored);
        activity?.SetTag("smartparking.analytics.rows", rows.Count);
        return report;
    }

    /// <summary>Milliseconds to whole seconds, half away from zero (ADR-0010: the rows store whole seconds).</summary>
    public static long ToSeconds(long milliseconds) => (long)Math.Round(milliseconds / 1000d, MidpointRounding.AwayFromZero);

    private static int Hours(DateTimeOffset from, DateTimeOffset to) => (int)((to - from).Ticks / OccupancyIntervalCalculator.Hour.Ticks);

    private static void EnsureRange(DateTimeOffset from, DateTimeOffset to)
    {
        if (!OccupancyIntervalCalculator.IsWholeHour(from) || !OccupancyIntervalCalculator.IsWholeHour(to) || to <= from)
        {
            throw new ArgumentException("The range must be whole UTC hours with from < to.", nameof(to));
        }
    }
}
