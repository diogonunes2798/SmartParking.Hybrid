using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Spots;

internal static class SpotErrors
{
    public static Error NotFound(Guid facilityId, Guid spotId) =>
        Error.NotFound("Spot.NotFound", $"Parking spot {spotId} does not exist in parking facility {facilityId}.");

    /// <summary>A zone that is not in the spot's facility (or does not exist): 400, not the FK's 409 (spec 001; plan 001b, point 3).</summary>
    public static Error ZoneNotInFacility(IReadOnlyDictionary<string, string[]> fields) =>
        Error.Validation("Spot.ZoneNotInFacility", "The zone does not belong to the spot's parking facility.", fields);

    public static Error ZoneNotInFacility(string field) =>
        ZoneNotInFacility(new Dictionary<string, string[]> { [field] = ["The zone does not belong to this parking facility."] });
}
