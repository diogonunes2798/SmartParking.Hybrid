using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions.Events;

/// <summary>A session opened on a spot, priced by the tariff version in force at that instant (ADR 0005).</summary>
[DomainEvent("SessionOpened", 1)]
public sealed record SessionOpened(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    Guid TariffId,
    Guid? ReservationId,
    DateTimeOffset StartedAt) : IDomainEvent;
