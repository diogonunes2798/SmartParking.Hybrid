namespace SmartParking.SensorSimulator.Wire;

/// <summary>Body of <c>POST /api/facilities</c> (spec 001).</summary>
internal sealed record CreateFacilityRequest(
    Guid OperatorId,
    string Name,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZone,
    IReadOnlyList<OpeningHoursRequest> OpeningHours);
