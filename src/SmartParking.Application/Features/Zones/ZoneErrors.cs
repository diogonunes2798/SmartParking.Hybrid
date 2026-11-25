using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Zones;

internal static class ZoneErrors
{
    public static Error NotFound(Guid facilityId, Guid zoneId) =>
        Error.NotFound("Zone.NotFound", $"Zone {zoneId} does not exist in parking facility {facilityId}.");
}
