namespace SmartParking.Application.Analytics;

/// <summary>
/// A facility as the aggregation sees it: its zone (copied into each summary row, so that the report never joins the
/// operational tables) and its registered spots (the denominator of the occupancy rate, ADR-0010). TODO(Diogo): every
/// registered spot counts, with or without an Active sensor (review of Phase 6, finding 10); decide in Cap. 3.
/// </summary>
public sealed record AnalyticsFacility(Guid FacilityId, string Zone, IReadOnlyList<Guid> SpotIds);

/// <summary>
/// Configuration snapshot for the aggregation (default: the relational store, RF-06). Only the worker reads it; the
/// report reads the summary tables alone (UC-04, spec §4).
/// </summary>
public interface IAnalyticsFacilitySource
{
    Task<IReadOnlyList<AnalyticsFacility>> GetFacilitiesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One row of the hourly summary table (D4 default, ADR-0010): one facility in one closed UTC hour. The rate is not
/// stored: it is <see cref="OccupiedSeconds"/> / <see cref="AvailableSeconds"/>, so that any coarser period is the
/// ratio of the sums (a 23- or 25-hour day stays exact).
/// </summary>
/// <param name="SpotCount">Registered spots when the hour was aggregated (kept so the row can be explained later).</param>
/// <param name="AvailableSeconds"><see cref="SpotCount"/> × 3600.</param>
public sealed record OccupancyHourlySummary(
    Guid FacilityId,
    string Zone,
    DateTimeOffset HourStart,
    int SpotCount,
    long OccupiedSeconds,
    long AvailableSeconds,
    int EventCount,
    DateTimeOffset AggregatedAt);

/// <summary>
/// Write side of the summary tables (default: PostgreSQL schema <c>analytics</c>). <see cref="SaveAsync"/> is an upsert
/// keyed by (facility, hour): saving the same window twice leaves the same rows (idempotent, ADR-0010).
/// </summary>
public interface IOccupancySummaryStore
{
    /// <summary>End (exclusive) of the last range aggregated by the worker, or null before the first run.</summary>
    Task<DateTimeOffset?> GetCheckpointAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Upserts the rows and, if given, moves the checkpoint forward (never back), in one transaction. A row is replaced
    /// only by one aggregated at the same time or later, so a slow instance never overwrites a newer result (review of
    /// Phase 6, finding 11). Returns the checkpoint stored after the save (null if there is none).
    /// </summary>
    Task<DateTimeOffset?> SaveAsync(IReadOnlyList<OccupancyHourlySummary> rows, DateTimeOffset? checkpoint, CancellationToken cancellationToken);
}

/// <summary>Settings of the occupancy aggregation (section <c>Analytics</c>, ADR-0010).</summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>False disables the aggregation worker in this process.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the worker looks for newly closed hours.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// An hour is closed only this long after it ends, so that events still on their way are in the log. Provisional.
    /// TODO(Diogo): confirm with the Phase 9 measurements of sensor → MongoDB (<c>smartparking.occupancy_log.lag</c> plus
    /// the sensor-to-API part), not of D7, which ends in Redis (review of Phase 6, finding 4).
    /// </summary>
    public TimeSpan AllowedLateness { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Closed hours aggregated again on every run, to absorb events later than <see cref="AllowedLateness"/>. The upsert
    /// makes this safe; a later event older than this is only counted by an explicit <c>aggregate-occupancy</c>.
    /// </summary>
    public int ReaggregateHours { get; set; } = 2;

    /// <summary>On the very first run (no checkpoint), how many closed hours before now are aggregated.</summary>
    public int InitialHours { get; set; } = 24;

    /// <summary>Upper bound of hours aggregated in one run (a long backlog is caught up over several runs).</summary>
    public int MaxHoursPerRun { get; set; } = 24;

    /// <summary>Wait after a failed run (a database down).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (Interval <= TimeSpan.Zero || RetryDelay <= TimeSpan.Zero || AllowedLateness < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName} intervals must be positive.");
        }

        if (ReaggregateHours < 0 || InitialHours < 1 || MaxHoursPerRun < 1 || MaxHoursPerRun < ReaggregateHours + 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}: ReaggregateHours >= 0, InitialHours >= 1 and MaxHoursPerRun > ReaggregateHours.");
        }
    }
}
