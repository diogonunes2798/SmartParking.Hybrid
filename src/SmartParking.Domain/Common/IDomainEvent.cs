namespace SmartParking.Domain.Common;

/// <summary>
/// Fact that happened in the domain and must be propagated to other stores (read model, aggregates).
/// In the relational sub-layer, events are written to the transactional outbox in the same transaction
/// as the change that raised them (spec §5, ADR-0004). Events carry identifiers and amounts only, never
/// personal data (RNF-06, data minimisation).
/// </summary>
public interface IDomainEvent
{
    /// <summary>Id of the aggregate that raised the event (ordering and idempotency key for consumers).</summary>
    Guid AggregateId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Aggregate that records domain events. Exposed through a method, not a property, so EF Core does not
/// try to map the pending events.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>Returns the pending events in the order they were raised and clears them.</summary>
    IReadOnlyList<IDomainEvent> DequeueDomainEvents();
}
