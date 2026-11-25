using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users.Events;

/// <summary>A user registered a vehicle; the licence plate is normalised.</summary>
[DomainEvent("VehicleRegistered", 1)]
public sealed record VehicleRegistered(
    Guid Id,
    Guid UserId,
    string LicensePlate) : IDomainEvent;
