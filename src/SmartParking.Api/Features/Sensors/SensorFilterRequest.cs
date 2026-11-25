using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Sensors;

/// <summary>Optional query filters of <c>GET /api/sensors</c>: <c>facilityId</c> and <c>kind</c>.</summary>
internal sealed record SensorFilterRequest(Guid? FacilityId, string? Kind)
{
    public SensorFilter ToFilter() => new(FacilityId, EnumRules.ParseOptional<SensorKind>(Kind));
}
