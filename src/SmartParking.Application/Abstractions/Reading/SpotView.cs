using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a parking spot (configuration only: no occupancy). <see cref="Version"/> is the <c>xmin</c>.</summary>
public sealed record SpotView(Guid Id, Guid FacilityId, Guid ZoneId, string Code, SpotType Type, SpotStatus Status, uint Version)
{
    public static SpotView From(ParkingSpot spot)
    {
        ArgumentNullException.ThrowIfNull(spot);
        return new SpotView(spot.Id, spot.FacilityId, spot.ZoneId, spot.Code, spot.Type, spot.Status, spot.Version);
    }
}
