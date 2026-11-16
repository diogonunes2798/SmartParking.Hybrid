namespace SmartParking.Infrastructure.Relational.Monolithic;

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
