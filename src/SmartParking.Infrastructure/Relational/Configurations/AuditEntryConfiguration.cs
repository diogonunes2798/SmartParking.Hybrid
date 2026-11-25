using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Infrastructure.Relational.Auditing;

namespace SmartParking.Infrastructure.Relational.Configurations;

/// <summary>Append-only audit trail, without foreign keys (spec 001). Permissions come from <c>0001_app_role.sql</c>.</summary>
internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Actor).HasMaxLength(AuditEntry.ActorMaxLength);
        builder.Property(entry => entry.EntityType).HasMaxLength(AuditEntry.EntityTypeMaxLength);
        builder.Property(entry => entry.Before).HasColumnType("jsonb");
        builder.Property(entry => entry.After).HasColumnType("jsonb");
        builder.Property(entry => entry.TraceId).HasMaxLength(AuditEntry.TraceIdMaxLength);

        builder.HasIndex(entry => new { entry.EntityType, entry.EntityId });
        builder.HasIndex(entry => entry.OutboxMessageId);
    }
}
