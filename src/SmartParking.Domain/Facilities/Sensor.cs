using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Sensor <em>configuration</em> (spec §3, RF-06). Its readings are occupancy events stored in MongoDB;
/// this split resolves the «Sensor» ambiguity raised by the supervisor. At most one sensor per spot.
/// </summary>
public sealed class Sensor : IVersioned
{
    public const int ModelMaxLength = 100;
    public const int FirmwareVersionMaxLength = 50;

    private Sensor()
    {
        Model = FirmwareVersion = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid SpotId { get; private set; }

    public string Model { get; private set; }

    public string FirmwareVersion { get; private set; }

    public SensorStatus Status { get; private set; }

    public uint Version { get; private set; }

    public static Sensor Create(Guid spotId, string model, string firmwareVersion, SensorStatus status)
    {
        var sensor = new Sensor { Id = Guid.CreateVersion7() };
        sensor.Update(spotId, model, firmwareVersion, status);
        return sensor;
    }

    /// <summary>Also used to move a sensor to another spot (replacement, maintenance).</summary>
    public void Update(Guid spotId, string model, string firmwareVersion, SensorStatus status)
    {
        SpotId = Guard.Id(spotId, nameof(spotId));
        Model = Guard.Text(model, nameof(model), ModelMaxLength);
        FirmwareVersion = Guard.Text(firmwareVersion, nameof(firmwareVersion), FirmwareVersionMaxLength);
        Status = Guard.Defined(status, nameof(status));
    }
}
