using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Infrastructure.Relational.Outbox;

namespace SmartParking.Infrastructure.Relational.Configurations;

/// <summary>Transactional outbox (D2), without foreign keys. The partial index serves the outbox reader (F3/F4).</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", table =>
            table.HasCheckConstraint("ck_outbox_messages_attempts", "attempts >= 0"));

        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();

        builder.Property(message => message.Type).HasMaxLength(OutboxMessage.TypeMaxLength);
        builder.Property(message => message.Payload).HasColumnType("jsonb");
        builder.Property(message => message.TraceId).HasMaxLength(OutboxMessage.TraceIdMaxLength);

        builder.HasIndex(message => message.OccurredAt)
            .HasDatabaseName("ix_outbox_messages_unpublished")
            .HasFilter("published_at IS NULL");
    }
}
