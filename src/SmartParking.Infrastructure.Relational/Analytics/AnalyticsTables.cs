using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Analytics;

/// <summary>
/// One row of <c>analytics.occupancy_hourly</c>, the hourly summary table of UC-04 (D4 default, ADR-0010): one facility
/// in one closed UTC hour. Mapped only so that the migrations create and version the table; it is written with an SQL
/// upsert and read with one SQL query (<see cref="EfOccupancySummaryStore"/>, <see cref="EfOccupancyReportQueries"/>).
/// <para>
/// The table lives in its own schema, <c>analytics</c>: nothing in it references the operational tables (no foreign
/// keys), so that the analytical load and its access rights stay apart from them (isolation, RF-05).
/// </para>
/// </summary>
public sealed class OccupancyHourlyRow
{
    public required Guid FacilityId { get; init; }

    /// <summary>Start of the UTC hour.</summary>
    public required DateTimeOffset HourStart { get; init; }

    /// <summary>Zone of the facility when the hour was aggregated (copied, so the report needs no join).</summary>
    public required string Zone { get; init; }

    public required int SpotCount { get; init; }

    public required long OccupiedSeconds { get; init; }

    /// <summary><see cref="SpotCount"/> × 3600.</summary>
    public required long AvailableSeconds { get; init; }

    public required int EventCount { get; init; }

    public required DateTimeOffset AggregatedAt { get; init; }
}

/// <summary>Position of an aggregation worker (<c>analytics.aggregation_checkpoints</c>).</summary>
public sealed class AggregationCheckpoint
{
    public const int NameMaxLength = 100;

    public required string Name { get; init; }

    /// <summary>End (exclusive) of the last range aggregated.</summary>
    public required DateTimeOffset Position { get; init; }
}

internal sealed class OccupancyHourlyRowConfiguration : IEntityTypeConfiguration<OccupancyHourlyRow>
{
    public const string Schema = "analytics";
    public const string TableName = "occupancy_hourly";
    public const string ZoneHourIndexName = "ix_occupancy_hourly_zone_hour_start";

    public void Configure(EntityTypeBuilder<OccupancyHourlyRow> builder)
    {
        builder.ToTable(TableName, Schema, table =>
        {
            table.HasCheckConstraint("ck_occupancy_hourly_spot_count", "spot_count >= 0");
            table.HasCheckConstraint("ck_occupancy_hourly_available", "available_seconds = spot_count::bigint * 3600");
            table.HasCheckConstraint("ck_occupancy_hourly_occupied", "occupied_seconds BETWEEN 0 AND available_seconds");
            table.HasCheckConstraint("ck_occupancy_hourly_events", "event_count >= 0");
        });
        // Index review of Phase 6 (ef-index-advisor, ADR-0010, bench/analytics/explain-report.sql):
        // - the key starts with hour_start, so the report without a zone (a range of hours only) is an index range scan
        //   instead of a sequential scan; the upsert's ON CONFLICT (facility_id, hour_start) matches the same column set;
        // - the report of one zone is an equality on zone then a range on hour_start, and reads only the three sums:
        //   (zone, hour_start) INCLUDE (sums) answers it from the index (index-only scan once the table is vacuumed).
        // No other index: the aggregation never reads this table, and each index costs on every upsert.
        builder.HasKey(r => new { r.HourStart, r.FacilityId });
        builder.Property(r => r.Zone).HasMaxLength(ParkingFacility.ZoneMaxLength);
        builder.HasIndex(r => new { r.Zone, r.HourStart })
            .IncludeProperties(r => new { r.EventCount, r.OccupiedSeconds, r.AvailableSeconds })
            .HasDatabaseName(ZoneHourIndexName);
    }
}

internal sealed class AggregationCheckpointConfiguration : IEntityTypeConfiguration<AggregationCheckpoint>
{
    public const string TableName = "aggregation_checkpoints";

    public void Configure(EntityTypeBuilder<AggregationCheckpoint> builder)
    {
        builder.ToTable(TableName, OccupancyHourlyRowConfiguration.Schema);
        builder.HasKey(c => c.Name);
        builder.Property(c => c.Name).HasMaxLength(AggregationCheckpoint.NameMaxLength);
    }
}
