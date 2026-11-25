namespace SmartParking.Domain.Common;

/// <summary>
/// A fact raised by an aggregate. Every implementation is a <c>sealed record</c> marked with
/// <see cref="DomainEventAttribute"/>, which names its outbox type (spec 001, AC-21).
/// </summary>
#pragma warning disable CA1040 // Marker interface: domain events have no common members.
public interface IDomainEvent;
#pragma warning restore CA1040
