using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Retention;

/// <summary>
/// Settings of the retention of the raw occupancy events (RNF-05, Phase 10, ADR-0013), section <c>Retention</c>.
/// The order of the steps is fixed by <see cref="OccupancyRetention"/>: a UTC day is archived only once its 24 hours are
/// aggregated (UC-04), and events are removed only once they are archived and the state of every spot before the purge
/// horizon is kept in a snapshot.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>False: nothing is archived and nothing is removed (the MongoDB TTL index stays at its «never» value).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long raw events stay in the operational store after their timestamp. Provisional: the specification has no
    /// value. TODO(Diogo): confirm in Cap. 3/4 (30 days chosen on 17-11-2026 as the provisional default).
    /// </summary>
    public TimeSpan RawEvents { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// A day is archived only this long after it ends, so that late events (up to <c>Ingestion:MaxEventAge</c>) are in
    /// the file. Must be at least <c>Ingestion:MaxEventAge</c>. TODO(Diogo): provisional (8 days).
    /// </summary>
    public TimeSpan ArchiveDelay { get; set; } = TimeSpan.FromDays(8);

    /// <summary>
    /// Safety margin of the MongoDB TTL guard: <c>expireAfterSeconds</c> is kept at (now − purge horizon) + margin, so the
    /// TTL monitor never passes the horizon while the Workers run at least once per margin. If the Workers stop for
    /// longer, the TTL monitor keeps deleting and may pass the horizon. TODO(Diogo): provisional (1 day).
    /// </summary>
    public TimeSpan GuardMargin { get; set; } = TimeSpan.FromDays(1);

    /// <summary>The purge horizon moves in steps of at least this much (fewer snapshots and fewer <c>collMod</c>).</summary>
    public TimeSpan HorizonStep { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often the Workers run the retention.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound of days archived in one run (a long backlog is caught up over several runs).</summary>
    public int MaxDaysPerRun { get; set; } = 7;

    /// <summary>Folder of the archive files (relative to the working directory of the Workers).</summary>
    public string ArchivePath { get; set; } = "archive/occupancy-events";

    public void Validate()
    {
        if (RawEvents < TimeSpan.FromHours(1) || ArchiveDelay < TimeSpan.Zero || GuardMargin <= TimeSpan.Zero
            || HorizonStep <= TimeSpan.Zero || Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}: RawEvents >= 1 h, ArchiveDelay >= 0, and GuardMargin, HorizonStep, Interval positive.");
        }

        if (MaxDaysPerRun < 1 || string.IsNullOrWhiteSpace(ArchivePath))
        {
            throw new InvalidOperationException($"{SectionName}: MaxDaysPerRun >= 1 and ArchivePath required.");
        }
    }
}

/// <summary>
/// Retention side of the raw event store (MongoDB collection or monolithic partitions). The append side is
/// <see cref="IOccupancyEventLog"/>; this interface only reads whole UTC days and enforces a purge horizon.
/// </summary>
public interface IRawEventRetentionStore
{
    /// <summary>Timestamp of the oldest event still stored, or null when the store is empty.</summary>
    Task<DateTimeOffset?> GetEarliestTimestampAsync(CancellationToken cancellationToken);

    /// <summary>Events with <c>since &lt;= timestamp &lt; until</c>.</summary>
    Task<long> CountAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>The events with <c>since &lt;= timestamp &lt; until</c>, each as one line of JSON (the archive format).</summary>
    IAsyncEnumerable<string> ExportAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>
    /// Lets the store remove events older than <paramref name="horizon"/> and nothing newer. Null: no horizon yet, nothing
    /// may be removed. MongoDB sets the TTL index; the monolithic variant drops the partitions that end at or before it.
    /// </summary>
    Task<EnforcementResult> EnforceAsync(DateTimeOffset? horizon, CancellationToken cancellationToken);
}

/// <summary>What one enforcement did.</summary>
/// <param name="ExpireAfterSeconds">MongoDB: the TTL value in force after the call (null for the monolithic variant).</param>
/// <param name="DroppedPartitions">Monolithic: partitions dropped by this call.</param>
public sealed record EnforcementResult(long? ExpireAfterSeconds, IReadOnlyList<string> DroppedPartitions);

/// <summary>
/// Progress of the retention in PostgreSQL (schema <c>analytics</c>, both variants): the aggregation coverage written by
/// the UC-04 aggregation, the archive manifest, and two positions that only move forward.
/// </summary>
public interface IRetentionLedger
{
    /// <summary>Number of distinct whole hours of [since, until) that the aggregation has covered (UC-04).</summary>
    Task<int> CountAggregatedHoursAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>End (exclusive) of the contiguous archived range, or null before the first archive.</summary>
    Task<DateTimeOffset?> GetArchivedUntilAsync(CancellationToken cancellationToken);

    /// <summary>Records one archived day and moves the archived position to its end (never back), in one transaction.</summary>
    Task RecordArchiveAsync(ArchivedDay day, CancellationToken cancellationToken);

    /// <summary>
    /// Events older than this may already be removed from the operational store, or null. Aggregations that start
    /// before it are refused: they would read an incomplete log.
    /// </summary>
    Task<DateTimeOffset?> GetPurgeHorizonAsync(CancellationToken cancellationToken);

    /// <summary>Moves the purge horizon forward (never back) and returns the stored value.</summary>
    Task<DateTimeOffset> AdvancePurgeHorizonAsync(DateTimeOffset horizon, CancellationToken cancellationToken);
}

/// <summary>One archived UTC day.</summary>
public sealed record ArchivedDay(DateOnly Day, string File, long Events, long Bytes, string Sha256, DateTimeOffset ArchivedAt);

/// <summary>
/// The last event of each spot before the purge horizon (PostgreSQL, both variants). Without it, a spot whose last event
/// was removed would start an aggregation as free (ADR-0010 reads the last event «however old») and the read model rebuild
/// (ADR-0008) would lose it.
/// </summary>
public interface ISpotStateSnapshotStore
{
    /// <summary>Upserts the entries; an entry is replaced only by a newer event of the same spot.</summary>
    Task SaveAsync(IReadOnlyList<SpotSnapshot> entries, CancellationToken cancellationToken);

    /// <summary>The entries of one facility.</summary>
    Task<IReadOnlyList<SpotSnapshot>> GetAsync(Guid facilityId, CancellationToken cancellationToken);

    /// <summary>Every entry.</summary>
    Task<IReadOnlyList<SpotSnapshot>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>One snapshot entry: the last event of a spot before the horizon.</summary>
public sealed record SpotSnapshot(Guid FacilityId, LatestSpotEvent Event);

/// <summary>Writes the archive file of one day.</summary>
public interface IEventArchive
{
    /// <summary>
    /// Writes the lines to the file of <paramref name="day"/> atomically (temporary file, then rename) and returns the
    /// file, the number of lines, the size and the SHA-256 of the compressed bytes. An existing file is replaced.
    /// </summary>
    Task<ArchiveFile> WriteDayAsync(DateOnly day, IAsyncEnumerable<string> lines, CancellationToken cancellationToken);

    /// <summary>Removes the file of <paramref name="day"/>, if any (an archive whose count did not match).</summary>
    void Delete(DateOnly day);
}

/// <summary>An archive file as written.</summary>
public sealed record ArchiveFile(string Path, long Lines, long Bytes, string Sha256);
