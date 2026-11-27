using SmartParking.Api.Common;
using SmartParking.Application.Features.Availability;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Availability;

/// <summary>Query string of <c>GET /api/availability</c> (spec 004): <c>facilityId</c> or <c>operatorId</c>, optional <c>zoneId</c> and <c>spotType</c>.</summary>
internal sealed record AvailabilityRequest(Guid? FacilityId, Guid? OperatorId, Guid? ZoneId, string? SpotType)
{
    public GetAvailability.Query ToQuery() => new(FacilityId, OperatorId, ZoneId, EnumRules.ParseOptional<SpotType>(SpotType));
}
