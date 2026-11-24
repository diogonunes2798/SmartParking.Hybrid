using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Parking spot. The facility never changes; the zone can change within the same facility (composite FK).
/// There is no occupancy here: free/occupied is Redis state (F5). Behaviour and events arrive in F2b.
/// </summary>
public sealed class ParkingSpot : AggregateRoot
{
    public const int CodeMaxLength = 32;

    private ParkingSpot()
    {
    }

    public Guid FacilityId { get; private init; }

    public Guid ZoneId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public SpotType Type { get; private set; }

    public SpotStatus Status { get; private set; }

    public uint Version { get; private set; }

    public static ParkingSpot Create(Guid id, Guid facilityId, Guid zoneId, string code, SpotType type) => new()
    {
        Id = Guard.Id(id),
        FacilityId = Guard.Id(facilityId),
        ZoneId = Guard.Id(zoneId),
        Code = Guard.Text(code, CodeMaxLength),
        Type = Guard.Defined(type),
        Status = SpotStatus.Active,
    };
}
