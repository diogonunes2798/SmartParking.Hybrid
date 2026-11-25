using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a registered sensor. <see cref="Version"/> is the <c>xmin</c>, sent as the ETag.</summary>
public sealed record SensorView(
    Guid Id,
    Guid FacilityId,
    string HardwareId,
    SensorKind Kind,
    string Model,
    DateTimeOffset InstalledAt,
    SensorStatus Status,
    Guid? SpotId,
    Guid? ZoneId,
    uint Version)
{
    public static SensorView From(Sensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        return new SensorView(
            sensor.Id, sensor.FacilityId, sensor.HardwareId, sensor.Kind, sensor.Model, sensor.InstalledAt,
            sensor.Status, sensor.SpotId, sensor.ZoneId, sensor.Version);
    }
}
