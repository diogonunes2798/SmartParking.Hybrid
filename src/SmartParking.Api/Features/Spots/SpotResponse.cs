using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Spots;

/// <summary>A parking spot on the wire (configuration only, no occupancy). The version travels in the <c>ETag</c> header.</summary>
internal sealed record SpotResponse(Guid Id, Guid FacilityId, Guid ZoneId, string Code, string Type, string Status)
{
    public static SpotResponse From(SpotView view) =>
        new(view.Id, view.FacilityId, view.ZoneId, view.Code, view.Type.ToString(), view.Status.ToString());
}
