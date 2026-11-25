using SmartParking.Application.Features.Sensors;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Sensors;

/// <summary>Body of <c>POST /api/sensors</c>. Nullable fields: a missing one is a 400.</summary>
internal sealed record RegisterSensorRequest(
    Guid FacilityId,
    string? HardwareId,
    string? Kind,
    string? Model,
    DateTimeOffset? InstalledAt,
    Guid? SpotId,
    Guid? ZoneId)
{
    public RegisterSensor.Command ToCommand() => new(
        FacilityId, HardwareId!, Enum.Parse<SensorKind>(Kind!), Model!, InstalledAt!.Value, SpotId, ZoneId);
}
