using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Infrastructure.Relational.Analytics;

namespace SmartParking.Infrastructure.Relational.Retention;

/// <summary>
/// One whole UTC hour covered by the UC-04 aggregation (<c>analytics.aggregated_hours</c>, Phase 10, ADR-0013), written by
/// <see cref="EfOccupancySummaryStore"/> in the same transaction as the summary rows. Retention archives and removes a day
/// only when its 24 hours are here. Mapped only so that the migrations create the table.
/// </summary>
public sealed class AggregatedHourRow
{
    public required DateTimeOffset HourStart { get; init; }

    public required DateTimeOffset AggregatedAt { get; init; }
}

/// <summary>
/// The last event of a spot before the purge horizon (<c>analytics.spot_state_snapshot</c>, ADR-0013). No foreign key to
/// the operational tables (same isolation as the summary table, ADR-0010).
/// </summary>
public sealed class SpotStateSnapshotRow
{
    public required Guid SpotId { get; init; }

    public required Guid FacilityId { get; init; }

    public required Guid EventId { get; init; }

    /// <summary>1 = OCCUPIED, 2 = FREED (the values of <c>OccupancyEventType</c>).</summary>
    public required short Type { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required DateTimeOffset SavedAt { get; init; }
}

/// <summary>Manifest of the archive files, one row per UTC day (<c>analytics.retention_archive</c>, ADR-0013).</summary>
public sealed class RetentionArchiveRow
{
    public required DateOnly Day { get; init; }

    /// <summary>Full path of the file; empty for a day without events.</summary>
    public required string File { get; init; }

    public required long Events { get; init; }

    public required long Bytes { get; init; }

    /// <summary>SHA-256 of the compressed file, lower-case hex; empty for a day without events.</summary>
    public required string Sha256 { get; init; }

    public required DateTimeOffset ArchivedAt { get; init; }
}

/// <summary>
/// Positions of the retention that only move forward (<c>analytics.retention_positions</c>): <c>archived_until</c> and
/// <c>purge_horizon</c>. A table of its own, not <c>aggregation_checkpoints</c>, so that the two never mix.
/// </summary>
public sealed class RetentionPositionRow
{
    public const string ArchivedUntil = "archived_until";
    public const string PurgeHorizon = "purge_horizon";

    public required string Name { get; init; }

    public required DateTimeOffset Position { get; init; }
}

internal sealed class AggregatedHourRowConfiguration : IEntityTypeConfiguration<AggregatedHourRow>
{
    public void Configure(EntityTypeBuilder<AggregatedHourRow> builder)
    {
        builder.ToTable("aggregated_hours", OccupancyHourlyRowConfiguration.Schema);
        builder.HasKey(r => r.HourStart);
    }
}

internal sealed class SpotStateSnapshotRowConfiguration : IEntityTypeConfiguration<SpotStateSnapshotRow>
{
    public void Configure(EntityTypeBuilder<SpotStateSnapshotRow> builder)
    {
        builder.ToTable("spot_state_snapshot", OccupancyHourlyRowConfiguration.Schema, table =>
            table.HasCheckConstraint("ck_spot_state_snapshot_type", "type IN (1, 2)"));
        builder.HasKey(r => r.SpotId);
        builder.HasIndex(r => r.FacilityId).HasDatabaseName("ix_spot_state_snapshot_facility_id");
    }
}

internal sealed class RetentionArchiveRowConfiguration : IEntityTypeConfiguration<RetentionArchiveRow>
{
    public void Configure(EntityTypeBuilder<RetentionArchiveRow> builder)
    {
        builder.ToTable("retention_archive", OccupancyHourlyRowConfiguration.Schema);
        builder.HasKey(r => r.Day);
        builder.Property(r => r.File).HasMaxLength(1024);
        builder.Property(r => r.Sha256).HasMaxLength(64);
    }
}

internal sealed class RetentionPositionRowConfiguration : IEntityTypeConfiguration<RetentionPositionRow>
{
    public void Configure(EntityTypeBuilder<RetentionPositionRow> builder)
    {
        builder.ToTable("retention_positions", OccupancyHourlyRowConfiguration.Schema);
        builder.HasKey(r => r.Name);
        builder.Property(r => r.Name).HasMaxLength(50);
    }
}
