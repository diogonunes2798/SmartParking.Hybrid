namespace SmartParking.Infrastructure.Messaging;

/// <summary>
/// Settings of one consumer group reader (configuration section <c>Messaging:Consumers:&lt;name&gt;</c>).
/// The defaults are the Phase 3 values recorded in ADR-0006; Phase 9 measures their effect.
/// </summary>
public sealed class RedisStreamConsumerOptions
{
    /// <summary>Stream to read (the bus topic).</summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>Consumer group. Each group receives every message once; groups are independent (Phase 4 adds one).</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Consumer name inside the group. It must be stable across restarts of the same process, so that on restart
    /// the consumer finds its own pending (unacknowledged) messages first, and unique per concurrent consumer.
    /// </summary>
    public string ConsumerName { get; set; } = Environment.MachineName;

    /// <summary>Entries per <c>XREADGROUP</c> (and per handler call, i.e. per MongoDB <c>insertMany</c>).</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Wait when the stream has nothing new. StackExchange.Redis does not support blocking reads (<c>BLOCK</c>) on its
    /// shared connection, so the consumer polls; this interval adds up to its value to the D7 latency.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How often to look for messages abandoned by consumers that died (<c>XAUTOCLAIM</c>).</summary>
    public TimeSpan ClaimInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum idle time before another consumer's pending message is claimed.</summary>
    public TimeSpan ClaimMinIdle { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound of the exponential back-off after a failure (Redis or handler unavailable).</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Stream where poison messages are copied before being acknowledged.</summary>
    public string DeadLetterStream => Stream + ":dead";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Stream) || string.IsNullOrWhiteSpace(Group) || string.IsNullOrWhiteSpace(ConsumerName))
        {
            throw new InvalidOperationException("A stream consumer needs Stream, Group and ConsumerName.");
        }

        if (BatchSize <= 0 || PollInterval <= TimeSpan.Zero || ClaimInterval <= TimeSpan.Zero
            || ClaimMinIdle <= TimeSpan.Zero || MaxBackoff <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Stream consumer sizes and intervals must be positive.");
        }
    }
}
