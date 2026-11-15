using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Parking spot (spec §3). Its current state (free/occupied) is deliberately absent:
/// it lives only in the Redis read model (UC-03), never in the relational store.
/// </summary>
public sealed class ParkingSpot : IVersioned
{
    public const int CodeMaxLength = 50;

    private ParkingSpot()
    {
        Code = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid FacilityId { get; private set; }

    /// <summary>Human-readable code, unique within the facility (e.g. "P1-042").</summary>
    public string Code { get; private set; }

    public VehicleType AllowedVehicleType { get; private set; }

    public uint Version { get; private set; }

    public static ParkingSpot Create(Guid facilityId, string code, VehicleType allowedVehicleType)
    {
        var spot = new ParkingSpot
        {
            Id = Guid.CreateVersion7(),
            FacilityId = Guard.Id(facilityId, nameof(facilityId)),
        };
        spot.Update(code, allowedVehicleType);
        return spot;
    }

    public void Update(string code, VehicleType allowedVehicleType)
    {
        Code = Guard.Text(code, nameof(code), CodeMaxLength);
        AllowedVehicleType = Guard.Defined(allowedVehicleType, nameof(allowedVehicleType));
    }
}
