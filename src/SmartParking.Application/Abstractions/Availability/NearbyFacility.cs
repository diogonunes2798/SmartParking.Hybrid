namespace SmartParking.Application.Abstractions.Availability;

/// <summary>A facility found by the radius search, with its distance to the search point (spec 004, RF-04).</summary>
public sealed record NearbyFacility(
    Guid FacilityId,
    string Name,
    Guid OperatorId,
    double DistanceMeters,
    int Free,
    int Total);
