using SmartParking.Application.Abstractions;
using SmartParking.Application.Common;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Messaging;

/// <summary>
/// <see cref="IEventBus"/> on Redis Streams (D2 default, ADR-0006). Each message is one <c>XADD</c> entry with the
/// fields <c>key</c>, <c>body</c> and (optionally) <c>traceparent</c>. The commands of a batch are sent without
/// waiting for each reply, so the multiplexer pipelines them into one round trip; Redis keeps their order.
/// The stream is not trimmed here (no <c>MAXLEN</c>): trimming could drop entries a slow consumer group has not
/// read yet. Retention is Phase 10 (RNF-05).
/// </summary>
internal sealed class RedisStreamsEventBus(RedisBusConnection connection) : IEventBus
{
    public async Task PublishAsync(string topic, IReadOnlyList<BusMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(messages);
        try
        {
            // The connection task is shared and cached: abandoning the wait on cancellation leaves nothing unobserved.
            var redis = await connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var db = redis.GetDatabase();
            var pending = new Task<RedisValue>[messages.Count];
            for (var i = 0; i < messages.Count; i++)
            {
                pending[i] = db.StreamAddAsync(topic, StreamFields.From(messages[i]));
            }

            // Once sent, XADD cannot be cancelled; wait for every reply (bounded by the client's AsyncTimeout, 5 s)
            // instead of abandoning the tasks on cancellation, so that no failure goes unobserved and the response
            // matches what Redis stored (review of Phase 3, finding 6).
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            throw new EventBusUnavailableException($"Redis did not accept the messages for stream '{topic}'.", ex);
        }
    }
}

/// <summary>Field names of a stream entry and the mapping to and from <see cref="BusMessage"/>.</summary>
internal static class StreamFields
{
    public const string Key = "key";
    public const string Body = "body";
    public const string TraceParent = "traceparent";

    public static NameValueEntry[] From(BusMessage message) =>
        message.TraceParent is null
            ? [new(Key, message.Key), new(Body, message.Body)]
            : [new(Key, message.Key), new(Body, message.Body), new(TraceParent, message.TraceParent)];

    /// <summary>Null when the entry has no body (deleted from the stream, or not written by this bus).</summary>
    public static BusMessage? To(StreamEntry entry)
    {
        if (entry.IsNull)
        {
            return null;
        }

        var body = entry[Body];
        if (body.IsNull)
        {
            return null;
        }

        var traceParent = entry[TraceParent];
        return new BusMessage(entry[Key].ToString(), body.ToString(), traceParent.IsNull ? null : traceParent.ToString());
    }
}
