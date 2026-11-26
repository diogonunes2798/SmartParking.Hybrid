using SmartParking.SensorSimulator.Generation;

namespace SmartParking.SensorSimulator.Sending;

/// <summary>
/// Batching of one virtual gateway (spec 003, round 6): a batch leaves when it reaches <c>maxBatchSize</c> events or
/// when <c>flushInterval</c> has passed since its oldest event was added, whichever comes first. The caller passes the
/// instant, so the rule needs no timer and is tested with a fake clock (AC-23).
/// </summary>
internal sealed class BatchAccumulator
{
    private readonly int maxBatchSize;

    private readonly TimeSpan flushInterval;

    private List<SimulatedEvent> buffer = [];

    private DateTimeOffset? oldestAddedAt;

    public BatchAccumulator(int maxBatchSize, TimeSpan flushInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushInterval, TimeSpan.Zero);

        this.maxBatchSize = maxBatchSize;
        this.flushInterval = flushInterval;
    }

    /// <summary>When the buffered events are due, or <see langword="null"/> when the buffer is empty.</summary>
    public DateTimeOffset? DueAt => oldestAddedAt + flushInterval;

    public int Count => buffer.Count;

    /// <summary>Adds an event; returns the full batch when this event completes it.</summary>
    public IReadOnlyList<SimulatedEvent>? Add(SimulatedEvent simulatedEvent, DateTimeOffset now)
    {
        buffer.Add(simulatedEvent);
        oldestAddedAt ??= now;
        return buffer.Count >= maxBatchSize ? Take() : null;
    }

    /// <summary>Returns the buffered events when the flush interval has passed, otherwise <see langword="null"/>.</summary>
    public IReadOnlyList<SimulatedEvent>? TakeDue(DateTimeOffset now) =>
        DueAt is { } due && now >= due ? Take() : null;

    /// <summary>Returns whatever is buffered (end of the run), or <see langword="null"/> when empty.</summary>
    public IReadOnlyList<SimulatedEvent>? TakeAll() => buffer.Count > 0 ? Take() : null;

    private List<SimulatedEvent> Take()
    {
        var batch = buffer;
        buffer = [];
        oldestAddedAt = null;
        return batch;
    }
}
