using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Zones;

/// <summary>A zone on the wire. The version travels in the <c>ETag</c> header.</summary>
internal sealed record ZoneResponse(Guid Id, Guid FacilityId, string Name)
{
    public static ZoneResponse From(ZoneView view) => new(view.Id, view.FacilityId, view.Name);
}
