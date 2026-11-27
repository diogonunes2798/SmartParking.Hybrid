namespace SmartParking.Application.Abstractions.Availability;

/// <summary>Free and total spots of a facility and of its zones, as the availability view holds them (spec 004).</summary>
public sealed record FacilityAvailability(
    Guid FacilityId,
    string Name,
    Guid OperatorId,
    int Free,
    int Total,
    IReadOnlyList<ZoneAvailability> Zones);
