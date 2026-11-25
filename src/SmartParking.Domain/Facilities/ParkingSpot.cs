using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Parking spot. The facility never changes; the zone can change within the same facility (composite FK).
/// There is no occupancy here: free/occupied is Redis state (F5). <c>Active</c>/<c>Inactive</c> is configuration.
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

    public static ParkingSpot Create(Guid id, Guid facilityId, Guid zoneId, string code, SpotType type)
    {
        var spot = new ParkingSpot
        {
            Id = Guard.Id(id),
            FacilityId = Guard.Id(facilityId),
            ZoneId = Guard.Id(zoneId),
            Code = Guard.Text(code, CodeMaxLength),
            Type = Guard.Defined(type),
            Status = SpotStatus.Active,
        };

        spot.Raise(new SpotCreated(spot.Id, spot.FacilityId, spot.ZoneId, spot.Code, spot.Type, spot.Status));
        return spot;
    }

    /// <summary>
    /// Replaces the editable fields (PUT). The caller checks that the zone belongs to the same facility (400);
    /// the composite FK guarantees it. Returns <c>false</c>, without raising an event, when nothing changed.
    /// </summary>
    public bool Update(Guid zoneId, string code, SpotType type, SpotStatus status)
    {
        var before = (ZoneId, Code, Type, Status);
        var previousZoneId = ZoneId;

        ZoneId = Guard.Id(zoneId);
        Code = Guard.Text(code, CodeMaxLength);
        Type = Guard.Defined(type);
        Status = Guard.Defined(status);

        if (before == (ZoneId, Code, Type, Status))
        {
            return false;
        }

        Raise(new SpotUpdated(Id, FacilityId, ZoneId, previousZoneId, Code, Type, Status));
        return true;
    }

    /// <summary>Records the deletion. The database refuses it (FK RESTRICT) while a sensor, reservation or session points at it.</summary>
    public void MarkForDeletion() => Raise(new SpotDeleted(Id, FacilityId, ZoneId, Code, Type, Status));
}
