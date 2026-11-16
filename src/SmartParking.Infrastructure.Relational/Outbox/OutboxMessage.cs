using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartParking.Infrastructure.Relational.Outbox;

/// <summary>
/// One row of the transactional outbox (spec §5, ADR-0004): a domain event written in the same transaction
/// as the change that raised it. A worker (Phase 5) publishes the unprocessed rows and sets
/// <see cref="ProcessedAt"/>; delivery is at-least-once, so consumers deduplicate by <see cref="Id"/>.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 100;

    /// <summary>A W3C <c>traceparent</c> is 55 characters (version 00); room for a future version.</summary>
    public const int TraceParentMaxLength = 128;

    public Guid Id { get; init; }

    /// <summary>Event name, e.g. <c>SessionClosed</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Aggregate that raised the event: keeps per-aggregate order for consumers.</summary>
    public required Guid AggregateId { get; init; }

    /// <summary>The event serialised as JSON (camelCase, enums by name).</summary>
    public required string Payload { get; init; }

    /// <summary>When the event happened in the domain (start of the consistency window, Cap. 4.5).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// W3C trace context of the request that raised the event (Phase 5, ADR-0009): the relay continues this trace, so
    /// it runs from the HTTP request to the consumer. Null when the change was made outside a traced operation.
    /// </summary>
    public string? TraceParent { get; init; }

    /// <summary>Null until the event is published (Phase 5).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string UnprocessedIndexName = "ix_outbox_messages_unprocessed";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength);
        builder.Property(m => m.Payload).HasColumnType("jsonb");
        builder.Property(m => m.TraceParent).HasMaxLength(OutboxMessage.TraceParentMaxLength);

        // The publisher of Phase 5 reads «unprocessed, oldest first». A partial index keeps only the
        // pending rows, so it stays small however long the outbox grows (processed rows leave the index).
        builder.HasIndex(m => m.OccurredAt)
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName(UnprocessedIndexName);
    }
}
