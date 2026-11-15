using System.Globalization;
using SmartParking.Application.Abstractions;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Messaging;

/// <summary>
/// Consumer group positions on Redis Streams (D2, ADR-0006), for the read model rebuild (ADR-0008, review of Phase 4,
/// finding 2). Stream entry ids are <c>&lt;ms&gt;-&lt;seq&gt;</c>; a group's <c>last-delivered-id</c> is the last entry
/// handed to any of its consumers, and its pending entries (PEL) are those delivered but not acknowledged.
/// </summary>
internal sealed class RedisConsumerGroupPositions(RedisBusConnection connection) : IConsumerGroupPositions
{
    private const string Start = "0-0";

    public async Task<string> GetAcknowledgedPositionAsync(string topic, string group, CancellationToken cancellationToken)
    {
        var db = (await connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).GetDatabase();
        if (!await db.KeyExistsAsync(topic).ConfigureAwait(false))
        {
            return Start;
        }

        var info = (await db.StreamGroupInfoAsync(topic).ConfigureAwait(false)).FirstOrDefault(g => g.Name == group);
        if (info.Name is null)
        {
            return Start; // the group never ran: nothing of the stream is known to be acknowledged
        }

        if (info.PendingMessageCount > 0)
        {
            var pending = await db.StreamPendingAsync(topic, group).ConfigureAwait(false);
            return Before(pending.LowestPendingMessageId.ToString());
        }

        return info.LastDeliveredId ?? Start;
    }

    public async Task SetPositionAsync(string topic, string group, string position, CancellationToken cancellationToken)
    {
        var db = (await connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).GetDatabase();
        try
        {
            await db.ExecuteAsync("XGROUP", "SETID", topic, group, position).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP", StringComparison.Ordinal)
            || ex.Message.Contains("requires the key to exist", StringComparison.Ordinal))
        {
            // No group yet (NOGROUP), or no stream yet (Redis 7.4: "The XGROUP subcommand requires the key to exist").
            await db.StreamCreateConsumerGroupAsync(topic, group, position, createStream: true).ConfigureAwait(false);
        }
    }

    /// <summary>The id just before <paramref name="id"/> (entries after it include <paramref name="id"/>).</summary>
    internal static string Before(string id)
    {
        var dash = id.IndexOf('-', StringComparison.Ordinal);
        var ms = ulong.Parse(id.AsSpan(0, dash), CultureInfo.InvariantCulture);
        var seq = ulong.Parse(id.AsSpan(dash + 1), CultureInfo.InvariantCulture);
        return seq > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ms}-{seq - 1}")
            : ms > 0 ? string.Create(CultureInfo.InvariantCulture, $"{ms - 1}-{ulong.MaxValue}") : Start;
    }
}
