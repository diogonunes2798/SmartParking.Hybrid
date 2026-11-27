using SmartParking.Application.Abstractions.Availability;

namespace SmartParking.Api.Features.Availability;

/// <summary>A facility of <c>GET /api/availability</c>; with <c>spotType</c>, every count is of that type only.</summary>
internal sealed record FacilityAvailabilityResponse(
    Guid FacilityId,
    string Name,
    Guid OperatorId,
    int Free,
    int Total,
    IReadOnlyList<ZoneAvailabilityResponse> Zones)
{
    public static FacilityAvailabilityResponse From(FacilityAvailability facility)
    {
        ArgumentNullException.ThrowIfNull(facility);

        return new(
            facility.FacilityId,
            facility.Name,
            facility.OperatorId,
            facility.Free,
            facility.Total,
            [.. facility.Zones.Select(zone => new ZoneAvailabilityResponse(zone.ZoneId, zone.Name, zone.Free, zone.Total))]);
    }
}
