using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Persistence;

namespace SmartParking.Application.Features.Sensors;

/// <summary>
/// Checks that a spot or zone belongs to the sensor's facility before saving, so the client gets 400 instead of the
/// composite FK's 409 (spec 001; plan 001b, point 3).
/// </summary>
internal static class SensorPlacement
{
    public static async Task<Error?> CheckSpotAsync(
        IParkingSpotRepository spots, Guid facilityId, Guid? spotId, CancellationToken cancellationToken)
    {
        if (spotId is not { } id)
        {
            return null;
        }

        var spot = await spots.GetByIdAsync(id, cancellationToken);
        return spot is null || spot.FacilityId != facilityId ? SensorErrors.SpotNotInFacility : null;
    }

    public static async Task<Error?> CheckZoneAsync(
        IZoneRepository zones, Guid facilityId, Guid? zoneId, CancellationToken cancellationToken)
    {
        if (zoneId is not { } id)
        {
            return null;
        }

        var zone = await zones.GetByIdAsync(id, cancellationToken);
        return zone is null || zone.FacilityId != facilityId ? SensorErrors.ZoneNotInFacility : null;
    }
}
