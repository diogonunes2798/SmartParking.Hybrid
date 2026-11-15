namespace SmartParking.Application.Abstractions;

/// <summary>
/// A message on the event bus (decision D2). <see cref="Key"/> identifies the business event (e.g. the
/// occupancy <c>eventId</c>) for logs and idempotency; <see cref="Body"/> is its JSON; <see cref="TraceParent"/>
/// carries the W3C trace context from the producer to the consumer (OpenTelemetry, D7 latency).
/// </summary>
public sealed record BusMessage(string Key, string Body, string? TraceParent = null);

/// <summary>A message as delivered to a consumer, with the bus-assigned identifier used to acknowledge it.</summary>
public sealed record DeliveredMessage(string Id, BusMessage Message);

/// <summary>
/// Publishing side of the event bus (D2, «EM ABERTO»). The default implementation is Redis Streams
/// (Infrastructure.Messaging, ADR-0006); Kafka would be another implementation of this interface.
/// Delivery to consumers is at least once, so every consumer must be idempotent (spec §5).
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Appends <paramref name="messages"/> to <paramref name="topic"/>, in order. Returns only when the bus has
    /// accepted all of them; throws <see cref="Common.EventBusUnavailableException"/> otherwise (the caller may
    /// retry: a partially published batch is harmless because consumers are idempotent).
    /// </summary>
    Task PublishAsync(string topic, IReadOnlyList<BusMessage> messages, CancellationToken cancellationToken);
}

/// <summary>
/// Consuming side of the event bus. The bus calls <see cref="HandleAsync"/> with a batch and acknowledges the
/// batch only after it returns. Throwing means "transient failure, deliver again": nothing is acknowledged.
/// Messages that can never be processed (malformed) are returned in <see cref="BatchOutcome.Poison"/> so the bus
/// can set them aside instead of redelivering them forever.
/// </summary>
public interface IMessageBatchHandler
{
    Task<BatchOutcome> HandleAsync(IReadOnlyList<DeliveredMessage> messages, CancellationToken cancellationToken);
}

/// <summary>Result of a handled batch: identifiers of the messages that can never be processed.</summary>
public sealed record BatchOutcome(IReadOnlyList<string> Poison)
{
    public static BatchOutcome AllProcessed { get; } = new([]);
}

/// <summary>
/// Positions of consumer groups on a topic (D2). Used by the read model rebuild (review of Phase 4, finding 2): the
/// rebuild reads the log written by one group and must restart another group from where that log is complete, so
/// that nothing acknowledged by the second group is missing from the rebuilt state.
/// </summary>
public interface IConsumerGroupPositions
{
    /// <summary>
    /// A position such that every message up to it (inclusive) has been acknowledged by <paramref name="group"/>:
    /// just before its oldest pending message, or its last delivered one if nothing is pending. The start of the
    /// topic if the group or the topic does not exist.
    /// </summary>
    Task<string> GetAcknowledgedPositionAsync(string topic, string group, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="group"/> deliver again every message after <paramref name="position"/> (creates the group
    /// there if missing). Its pending messages stay pending and are redelivered as usual.
    /// </summary>
    Task SetPositionAsync(string topic, string group, string position, CancellationToken cancellationToken);
}
