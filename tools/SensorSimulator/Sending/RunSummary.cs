namespace SmartParking.SensorSimulator.Sending;

/// <summary>
/// Totals of one emitting run, written to the console log at the end. Not a measurement: the simulator never writes
/// to <c>results/</c> (the F12 experiments measure on the Api side).
/// </summary>
internal sealed record RunSummary(
    DateTimeOffset Start,
    long Generated,
    long Batches,
    long Accepted,
    long Duplicates,
    long Rejected,
    long Retries,
    long NotDeliveredEvents);
