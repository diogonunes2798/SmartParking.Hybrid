using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users.Events;

/// <summary>A user removed a vehicle that no session or reservation refers to.</summary>
[DomainEvent("VehicleRemoved", 1)]
public sealed record VehicleRemoved(
    Guid Id,
    Guid UserId,
    string LicensePlate) : IDomainEvent;
