using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions.Events;

/// <summary>A session closed; the amount is computed with the session's tariff version.</summary>
[DomainEvent("SessionClosed", 1)]
public sealed record SessionClosed(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    Guid TariffId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    decimal Amount) : IDomainEvent;
