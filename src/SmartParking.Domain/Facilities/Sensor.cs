using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Registered sensor device. Only occupancy sensors can sit on a spot; spot and zone belong to the
/// sensor's facility (composite FKs). Behaviour and events arrive in F2b.
/// </summary>
public sealed class Sensor : AggregateRoot
{
    public const int HardwareIdMaxLength = 64;

    public const int ModelMaxLength = 100;

    private Sensor()
    {
    }

    public Guid FacilityId { get; private init; }

    /// <summary>Device identifier, unique across all facilities.</summary>
    public string HardwareId { get; private init; } = string.Empty;

    public SensorKind Kind { get; private init; }

    public string Model { get; private set; } = string.Empty;

    public DateTimeOffset InstalledAt { get; private init; }

    public SensorStatus Status { get; private set; }

    public Guid? SpotId { get; private set; }

    public Guid? ZoneId { get; private set; }

    public uint Version { get; private set; }

    public static Sensor Create(
        Guid id,
        Guid facilityId,
        string hardwareId,
        SensorKind kind,
        string model,
        DateTimeOffset installedAt,
        Guid? spotId,
        Guid? zoneId)
    {
        if (kind != SensorKind.Occupancy && spotId is not null)
        {
            throw new ArgumentException("Only occupancy sensors can be placed on a spot.", nameof(spotId));
        }

        return new Sensor
        {
            Id = Guard.Id(id),
            FacilityId = Guard.Id(facilityId),
            HardwareId = Guard.Text(hardwareId, HardwareIdMaxLength),
            Kind = Guard.Defined(kind),
            Model = Guard.Text(model, ModelMaxLength),
            InstalledAt = installedAt,
            Status = SensorStatus.Active,
            SpotId = spotId is { } spot ? Guard.Id(spot) : null,
            ZoneId = zoneId is { } zone ? Guard.Id(zone) : null,
        };
    }
}
