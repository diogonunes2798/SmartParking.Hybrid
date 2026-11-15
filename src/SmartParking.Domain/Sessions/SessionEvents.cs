using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

// Domain events of UC-02, written to the outbox (ADR-0004) and published in Phase 5.
// Only identifiers, instants and amounts: the user and the vehicle are left out on purpose (RNF-06, data
// minimisation); the consumers known so far (read model, aggregates) need the spot, not the driver.

/// <summary>A session was opened on a spot.</summary>
public sealed record SessionStarted(Guid SessionId, Guid SpotId, DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid AggregateId => SessionId;
}

/// <summary>A session was closed and priced.</summary>
public sealed record SessionClosed(
    Guid SessionId, Guid SpotId, Guid TariffId, DateTimeOffset StartedAt, decimal Cost, DateTimeOffset OccurredAt)
    : IDomainEvent
{
    public Guid AggregateId => SessionId;
}

/// <summary>The amount due of a closed session was paid (simulated payment, spec §10).</summary>
public sealed record PaymentRegistered(
    Guid PaymentId, Guid SessionId, decimal Amount, PaymentMethod Method, DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid AggregateId => SessionId;
}
