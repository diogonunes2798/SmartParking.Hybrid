using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Registered sensor device. Only occupancy sensors can sit on a spot; spot and zone belong to the
/// sensor's facility (composite FKs, checked first by the handlers). A sensor without a spot stays registered.
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

        var sensor = new Sensor
        {
            Id = Guard.Id(id),
            FacilityId = Guard.Id(facilityId),
            HardwareId = Guard.Text(hardwareId, HardwareIdMaxLength),
            Kind = Guard.Defined(kind),
            Model = Guard.Text(model, ModelMaxLength),
            InstalledAt = installedAt.ToUniversalTime(),
            Status = SensorStatus.Active,
            SpotId = spotId is { } spot ? Guard.Id(spot) : null,
            ZoneId = zoneId is { } zone ? Guard.Id(zone) : null,
        };

        sensor.Raise(new SensorRegistered(
            sensor.Id, sensor.FacilityId, sensor.HardwareId, sensor.Kind, sensor.Model, sensor.InstalledAt,
            sensor.Status, sensor.SpotId, sensor.ZoneId));
        return sensor;
    }

    /// <summary>Replaces model, status and zone (PUT). Returns <c>false</c>, without raising an event, when nothing changed.</summary>
    public bool Update(string model, SensorStatus status, Guid? zoneId)
    {
        var before = (Model, Status, ZoneId);

        Model = Guard.Text(model, ModelMaxLength);
        Status = Guard.Defined(status);
        ZoneId = zoneId is { } zone ? Guard.Id(zone) : null;

        if (before == (Model, Status, ZoneId))
        {
            return false;
        }

        Raise(new SensorUpdated(Id, FacilityId, HardwareId, Kind, Model, InstalledAt, Status, SpotId, ZoneId));
        return true;
    }

    /// <summary>
    /// Places the sensor on a spot of its facility. Returns <c>false</c>, without raising an event, when it is
    /// already there (spec 001). The unique index on <c>spot_id</c> refuses a spot that already has a sensor (409).
    /// </summary>
    public bool AssignTo(Guid spotId)
    {
        if (Kind != SensorKind.Occupancy)
        {
            throw new InvalidOperationException("Only occupancy sensors can be placed on a spot.");
        }

        Guard.Id(spotId);
        if (SpotId == spotId)
        {
            return false;
        }

        var previousSpotId = SpotId;
        SpotId = spotId;
        Raise(new SensorReassigned(Id, FacilityId, HardwareId, spotId, previousSpotId));
        return true;
    }

    /// <summary>Takes the sensor off its spot. Returns <c>false</c>, without raising an event, when it has none.</summary>
    public bool Unassign()
    {
        if (SpotId is not { } previousSpotId)
        {
            return false;
        }

        SpotId = null;
        Raise(new SensorUnassigned(Id, FacilityId, HardwareId, previousSpotId));
        return true;
    }

    /// <summary>Records the removal. A sensor still on a spot is taken off first (<c>DELETE /sensors/{id}/spot</c>).</summary>
    public void MarkForRemoval()
    {
        if (SpotId is not null)
        {
            throw new InvalidOperationException("Take the sensor off its spot before removing it.");
        }

        Raise(new SensorRemoved(Id, FacilityId, HardwareId, Kind, Model, Status, ZoneId));
    }
}
