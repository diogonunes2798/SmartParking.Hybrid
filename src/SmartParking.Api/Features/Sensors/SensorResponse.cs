using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Sensors;

/// <summary>A registered sensor on the wire. The version travels in the <c>ETag</c> header.</summary>
internal sealed record SensorResponse(
    Guid Id,
    Guid FacilityId,
    string HardwareId,
    string Kind,
    string Model,
    DateTimeOffset InstalledAt,
    string Status,
    Guid? SpotId,
    Guid? ZoneId)
{
    public static SensorResponse From(SensorView view) => new(
        view.Id, view.FacilityId, view.HardwareId, view.Kind.ToString(), view.Model, view.InstalledAt,
        view.Status.ToString(), view.SpotId, view.ZoneId);
}
