namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>Width of the partitions of <c>monolith.occupancy_events</c>.</summary>
public enum PartitionInterval
{
    Month,
    Day,
}

/// <summary>Settings of the monolithic variant (configuration section <c>Persistence:Monolithic</c>, ADR-0012).</summary>
public sealed class MonolithicOptions
{
    public const string SectionName = "Persistence:Monolithic";

    /// <summary>
    /// How often the event writer checks whether the UC-05 columns (<c>temperature</c>, <c>air_quality</c>, <c>noise</c>)
    /// exist. Until they do, generation-2 readings are not stored (explicit schema, no <c>jsonb</c>); once the migration
    /// is applied, they are stored from the next check on, without a restart. Default 5 s.
    /// </summary>
    public TimeSpan SchemaRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Monthly partitions kept ready ahead of the current UTC month (Workers, every day). Default 3.</summary>
    public int PartitionMonthsAhead { get; set; } = 3;

    /// <summary>
    /// Width of the partitions created by the Workers (Phase 10, ADR-0013). <see cref="PartitionInterval.Month"/> (the
    /// default of Phase 8) or <see cref="PartitionInterval.Day"/>: retention drops whole partitions, so daily partitions
    /// keep at most one extra day instead of up to one extra month. An empty monthly partition that overlaps the daily
    /// range is replaced by the daily partitions of its month; one with rows is kept.
    /// </summary>
    public PartitionInterval PartitionInterval { get; set; } = PartitionInterval.Month;

    /// <summary>Daily partitions kept ready ahead of the current UTC day (<see cref="PartitionInterval.Day"/>). Default 7.</summary>
    public int PartitionDaysAhead { get; set; } = 7;

    /// <summary>Daily partitions created behind the current UTC day, for loads of past events (benchmarks). Default 0.</summary>
    public int PartitionDaysBehind { get; set; }

    /// <summary>How often the Workers run the partition maintenance. Default 24 h.</summary>
    public TimeSpan PartitionMaintenanceInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Attempts of one event batch when PostgreSQL reports a deadlock (40P01) or a serialization failure (40001). Two
    /// batches that share spots lock their <c>spot_state</c> rows; a deadlock is rare (rows are locked in spot order)
    /// but possible. Default 3.
    /// </summary>
    public int MaxWriteAttempts { get; set; } = 3;

    public void Validate()
    {
        if (SchemaRefreshInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:SchemaRefreshInterval must be positive.");
        }

        if (PartitionMonthsAhead is < 1 or > 24)
        {
            throw new InvalidOperationException($"{SectionName}:PartitionMonthsAhead must be between 1 and 24.");
        }

        if (PartitionDaysAhead is < 1 or > 366 || PartitionDaysBehind is < 0 or > 366)
        {
            throw new InvalidOperationException($"{SectionName}: PartitionDaysAhead 1–366 and PartitionDaysBehind 0–366.");
        }

        if (PartitionMaintenanceInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:PartitionMaintenanceInterval must be positive.");
        }

        if (MaxWriteAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException($"{SectionName}:MaxWriteAttempts must be between 1 and 10.");
        }
    }
}
