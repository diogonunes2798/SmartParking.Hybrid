using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Spots;

/// <summary>Optional query filters of <c>GET /api/facilities/{facilityId}/spots</c>: <c>zoneId</c> and <c>status</c>.</summary>
internal sealed record SpotFilterRequest(Guid? ZoneId, string? Status)
{
    public SpotFilter ToFilter() => new(ZoneId, EnumRules.ParseOptional<SpotStatus>(Status));
}
