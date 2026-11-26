using SmartParking.SensorSimulator.Wire;

namespace SmartParking.SensorSimulator.Sending;

/// <summary>
/// Gateway settings (spec 003, round 6): a batch leaves at <see cref="BatchSize"/> events (at most the Api's 500) or
/// after <see cref="FlushInterval"/>; a 503 is retried at most <see cref="MaxRetries"/> times.
/// </summary>
internal sealed record SenderSettings(int BatchSize, TimeSpan FlushInterval, int MaxRetries)
{
    public static SenderSettings Default { get; } = new(OccupancyBatchRequest.MaxEvents, TimeSpan.FromSeconds(1), 10);
}
