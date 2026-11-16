namespace SmartParking.Application.Abstractions;

/// <summary>
/// One pending row of the transactional outbox (spec §5, ADR-0004) as the relay sees it (Phase 5, ADR-0009).
/// <see cref="OccurredAt"/> is the start of the consistency window; <see cref="TraceParent"/> is the W3C trace context
/// of the request that wrote the row, so the trace continues from the HTTP request to the consumer.
/// </summary>
public sealed record OutboxEntry(
    Guid Id, string Type, Guid AggregateId, string Payload, DateTimeOffset OccurredAt, string? TraceParent);

/// <summary>
/// Reading side of the outbox, implemented by the relational sub-layer (ADR-0004: the interfaces live in the
/// Application, so Messaging never depends on Relational). Several relay instances may run at once: a claimed batch
/// is locked (<c>FOR UPDATE SKIP LOCKED</c>), so two instances never get the same rows.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Opens a transaction and locks up to <paramref name="maxCount"/> unprocessed rows, oldest first, skipping rows
    /// locked by another instance. The batch must be disposed; until <see cref="IOutboxBatch.CompleteAsync"/> runs,
    /// disposing it rolls back and the rows stay unprocessed (delivered again: at least once).
    /// </summary>
    Task<IOutboxBatch> ClaimAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>Number of unprocessed rows (backlog gauge).</summary>
    Task<long> CountPendingAsync(CancellationToken cancellationToken);
}

/// <summary>A locked batch of outbox rows (see <see cref="IOutboxStore.ClaimAsync"/>).</summary>
public interface IOutboxBatch : IAsyncDisposable
{
    IReadOnlyList<OutboxEntry> Entries { get; }

    /// <summary>Marks every row of the batch as processed at <paramref name="processedAt"/> and commits.</summary>
    Task CompleteAsync(DateTimeOffset processedAt, CancellationToken cancellationToken);
}
