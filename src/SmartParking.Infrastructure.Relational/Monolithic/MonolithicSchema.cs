namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// Names of the objects of the monolithic variant (ADR-0012), created by hand-written SQL in the migrations
/// <c>AddMonolithicSchema</c> and <c>AddEnvironmentalColumns</c>. They live in their own schema, outside the EF Core
/// model: the hybrid variant never reads them (they exist, empty, in both modes, so that there is one migration history).
/// </summary>
public static class MonolithicSchema
{
    public const string Schema = "monolith";

    /// <summary>Occupancy events, <c>PARTITION BY RANGE ("timestamp")</c>, one partition per UTC month plus a DEFAULT one.</summary>
    public const string OccupancyEvents = Schema + ".occupancy_events";

    /// <summary>Current state of each spot (the last event applied), updated in the same transaction as the event insert.</summary>
    public const string SpotState = Schema + ".spot_state";

    /// <summary>Partition that receives events outside every monthly partition (e.g. very old timestamps).</summary>
    public const string DefaultPartition = Schema + ".occupancy_events_default";

    /// <summary>
    /// <c>monolith.ensure_occupancy_partitions(from_month date, months int)</c>: creates the missing monthly partitions and
    /// returns how many it created. Called by the migration and, every day, by the Workers.
    /// </summary>
    public const string EnsurePartitionsFunction = Schema + ".ensure_occupancy_partitions";

    /// <summary>
    /// <c>monolith.ensure_occupancy_day_partitions(from_day date, days int)</c> (Phase 10, migration <c>AddRetention</c>): creates
    /// the missing daily partitions <c>occupancy_events_dYYYYMMDD</c>; an empty monthly partition in the way is replaced by
    /// the daily partitions of its whole month.
    /// </summary>
    public const string EnsureDayPartitionsFunction = Schema + ".ensure_occupancy_day_partitions";

    /// <summary>UC-05 columns added by <c>AddEnvironmentalColumns</c> (snake_case of the payload fields).</summary>
    public static IReadOnlyList<string> EnvironmentalColumns { get; } = ["temperature", "air_quality", "noise"];

    /// <summary>
    /// Geography of a facility, written exactly like the expression of the GiST index <c>ix_parking_facilities_geography</c>:
    /// the planner uses an expression index only when the query repeats the same expression.
    /// </summary>
    public const string FacilityGeography = "(postgis.ST_SetSRID(postgis.ST_MakePoint(f.longitude, f.latitude), 4326)::postgis.geography)";

    /// <summary>Type of an event as stored (<c>smallint</c>): the values of <see cref="Domain.Occupancy.OccupancyEventType"/>.</summary>
    public const short Occupied = 1;

    /// <inheritdoc cref="Occupied"/>
    public const short Freed = 2;
}
