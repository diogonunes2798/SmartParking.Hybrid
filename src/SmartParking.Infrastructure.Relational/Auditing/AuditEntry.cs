using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartParking.Infrastructure.Relational.Auditing;

/// <summary>
/// One row of the audit log (RNF-06): who changed which entity, how and when.
/// Written by <see cref="AuditSaveChangesInterceptor"/> in the same transaction as the change.
/// Append-only by convention; the log itself is never audited.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public required string Actor { get; init; }

    /// <summary>CLR name of the entity (e.g. <c>ParkingFacility</c>).</summary>
    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }

    /// <summary><c>Created</c>, <c>Updated</c> or <c>Deleted</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>
    /// JSON object: new values (Created), old values (Deleted) or <c>{"old":…,"new":…}</c> per changed
    /// property (Updated). Personal data is masked.
    /// </summary>
    public required string Changes { get; init; }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Actor).HasMaxLength(200);
        builder.Property(a => a.EntityType).HasMaxLength(100);
        builder.Property(a => a.Operation).HasMaxLength(20);
        builder.Property(a => a.Changes).HasColumnType("jsonb");

        // History of one entity, and time-range inspection.
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
        builder.HasIndex(a => a.OccurredAt);
    }
}
