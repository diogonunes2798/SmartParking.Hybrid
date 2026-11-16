using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Writes events as the JSON of <c>POST /api/ingest/occupancy</c> (UC-01). It uses <see cref="Utf8JsonWriter"/>
/// straight into a reusable buffer, so serialising a batch allocates almost nothing (Phase 7 allocation review).
/// </summary>
internal static class EventJsonWriter
{
    /// <summary>Payload field with the sensor generation (1 or 2). Lets the log be queried by generation.</summary>
    public const string GenerationField = "sensorGeneration";

    public const string TemperatureField = "temperature";
    public const string AirQualityField = "airQuality";
    public const string NoiseField = "noise";

    /// <summary>Writes a JSON array with <paramref name="events"/>.</summary>
    /// <param name="runStart">Instant that corresponds to offset 0; the sensor timestamp is runStart + offset.</param>
    public static void WriteBatch(
        Utf8JsonWriter writer, ReadOnlySpan<SimulatedEvent> events, Topology topology, DateTimeOffset runStart)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(topology);
        writer.WriteStartArray();
        foreach (ref readonly var e in events)
        {
            WriteEvent(writer, e, topology, runStart);
        }

        writer.WriteEndArray();
        writer.Flush();
    }

    public static void WriteEvent(Utf8JsonWriter writer, in SimulatedEvent e, Topology topology, DateTimeOffset runStart)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(topology);
        writer.WriteStartObject();
        writer.WriteString("eventId", e.EventId);
        writer.WriteString("sensorId", topology.SensorIds[e.SpotIndex]);
        writer.WriteString("spotId", topology.SpotIds[e.SpotIndex]);
        writer.WriteString("facilityId", topology.FacilityIds[e.FacilityIndex]);
        writer.WriteString("type", e.Type);
        writer.WriteString("timestamp", runStart.AddTicks(e.OffsetTicks)); // ISO 8601 with offset (RF-01)
        WritePayload(writer, e);
        writer.WriteEndObject();
    }

    /// <summary>
    /// <c>payload</c>: the generation, plus the three environmental readings for generation 2 (UC-05). Decimal values
    /// are always written with one decimal place (21.0, not 21), so that MongoDB stores them as doubles and does not
    /// mix Int32 and Double in the same field (the type issue noted in the Phase 3 diary).
    /// </summary>
    private static void WritePayload(Utf8JsonWriter writer, in SimulatedEvent e)
    {
        writer.WriteStartObject("payload");
        writer.WriteNumber(GenerationField, e.Generation);
        if (e.Generation == 2)
        {
            WriteOneDecimal(writer, TemperatureField, e.Readings.Temperature);
            writer.WriteNumber(AirQualityField, e.Readings.AirQuality);
            WriteOneDecimal(writer, NoiseField, e.Readings.Noise);
        }

        writer.WriteEndObject();
    }

    private static void WriteOneDecimal(Utf8JsonWriter writer, string name, double value)
    {
        Span<byte> buffer = stackalloc byte[32];
        if (!value.TryFormat(buffer, out var written, "F1", CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"Could not format {name}.");
        }

        writer.WritePropertyName(name);
        writer.WriteRawValue(buffer[..written], skipInputValidation: true);
    }

    /// <summary>One event per line, with indices instead of ids (<c>--dry-run</c>; no topology needed).</summary>
    public static void WriteDryRunLine(Utf8JsonWriter writer, in SimulatedEvent e)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteNumber("sequence", e.Sequence);
        writer.WriteNumber("offsetMs", e.OffsetTicks / (double)TimeSpan.TicksPerMillisecond);
        writer.WriteNumber("facility", e.FacilityIndex);
        writer.WriteNumber("spot", e.SpotIndex);
        writer.WriteString("type", e.Type);
        writer.WriteString("eventId", e.EventId);
        WritePayload(writer, e);
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>A fresh writer over <paramref name="buffer"/> (reset between batches).</summary>
    public static Utf8JsonWriter CreateWriter(IBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { SkipValidation = true });
}
