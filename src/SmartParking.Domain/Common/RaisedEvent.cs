namespace SmartParking.Domain.Common;

/// <summary>
/// A pending domain event and the entities its transition changed. The unit of work writes one outbox message for the
/// event and one audit entry per changed entity, all pointing to that message (spec 002, ADR 0006).
/// </summary>
public sealed record RaisedEvent(IDomainEvent Event, IReadOnlyList<object> Changed);
