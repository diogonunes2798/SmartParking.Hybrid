using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Sensors;

internal static class SensorErrors
{
    public static Error NotFound(Guid id) => Error.NotFound("Sensor.NotFound", $"Sensor {id} does not exist.");

    /// <summary>A spot of another facility (or that does not exist): 400, not the FK's 409 (spec 001; plan 001b, point 3).</summary>
    public static Error SpotNotInFacility { get; } = Error.Validation(
        "Sensor.SpotNotInFacility",
        "The spot does not belong to the sensor's parking facility.",
        new Dictionary<string, string[]> { ["spotId"] = ["The spot does not belong to the sensor's parking facility."] });

    public static Error ZoneNotInFacility { get; } = Error.Validation(
        "Sensor.ZoneNotInFacility",
        "The zone does not belong to the sensor's parking facility.",
        new Dictionary<string, string[]> { ["zoneId"] = ["The zone does not belong to the sensor's parking facility."] });

    public static Error NotOccupancy { get; } = Error.Validation(
        "Sensor.NotOccupancy",
        "Only occupancy sensors can be placed on a spot.",
        new Dictionary<string, string[]> { ["spotId"] = ["Only occupancy sensors can be placed on a spot."] });

    public static Error StillOnSpot { get; } = Error.Conflict(
        "Sensor.StillOnSpot",
        "The sensor is still on a spot. Take it off first with DELETE /api/sensors/{id}/spot.");
}
