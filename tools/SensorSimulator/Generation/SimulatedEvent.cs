using System.Globalization;
using SmartParking.SensorSimulator.Wire;

namespace SmartParking.SensorSimulator.Generation;

/// <summary>One generated occupancy event. <see cref="Timestamp"/> is the device clock, in UTC and whole milliseconds.</summary>
internal sealed record SimulatedEvent(
    Guid EventId,
    string HardwareId,
    Guid SpotId,
    Guid FacilityId,
    SimulatedEventType Type,
    DateTimeOffset Timestamp)
{
    /// <summary>ISO 8601 with <c>Z</c> and 3 decimals: inside the F4a format (at most 7 decimals, MongoDB keeps milliseconds).</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public OccupancyEventRequest ToRequest() => new(
        EventId,
        HardwareId,
        SpotId,
        Type.ToString(),
        Timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
}
