namespace SmartParking.Api.Features.Facilities;

/// <summary>A parking facility on the wire. The version travels in the <c>ETag</c> header, not in the body.</summary>
internal sealed record FacilityResponse(
    Guid Id,
    Guid OperatorId,
    string Name,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZone,
    string Status,
    IReadOnlyList<OpeningHoursResponse> OpeningHours);
