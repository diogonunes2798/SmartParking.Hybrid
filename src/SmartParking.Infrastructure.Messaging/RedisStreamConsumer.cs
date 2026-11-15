using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Diagnostics;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Messaging;

/// <summary>
/// Reads one consumer group of a Redis stream and hands batches to <typeparamref name="THandler"/>
/// (D2 default, ADR-0006). Delivery is at least once:
/// <list type="number">
/// <item>the group is created if missing (<c>XGROUP CREATE … MKSTREAM</c>, from the start of the stream);</item>
/// <item>on start, and after any failure, the consumer first re-reads <em>its own</em> pending entries
/// (<c>XREADGROUP … 0</c>): those were delivered before but never acknowledged (e.g. the process stopped
/// between the MongoDB write and the <c>XACK</c>); only when none are left does it read new ones (<c>&gt;</c>);</item>
/// <item><c>XACK</c> is sent only after the handler returns; if it throws, nothing is acknowledged, the consumer
/// backs off and re-reads its pending entries;</item>
/// <item>every <see cref="RedisStreamConsumerOptions.ClaimInterval"/>, entries idle for longer than
/// <see cref="RedisStreamConsumerOptions.ClaimMinIdle"/> in other consumers (which died) are claimed
/// (<c>XAUTOCLAIM</c>) and processed as own pending entries;</item>
/// <item>poison entries (malformed, as reported by the handler) are copied to the dead-letter stream and acknowledged.
/// Transient failures never dead-letter a message, however often they repeat (RNF-03: no event loss).</item>
/// </list>
/// The handler is resolved in a new DI scope per batch.
/// </summary>
public sealed class RedisStreamConsumer<THandler> : BackgroundService
    where THandler : IMessageBatchHandler
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(200);

    private readonly RedisBusConnection connection;
    private readonly IServiceScopeFactory scopes;
    private readonly RedisStreamConsumerOptions options;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly Counter<long> handled;
    private readonly Counter<long> failures;
    private readonly KeyValuePair<string, object?> groupTag;
    private int consecutiveFailures;

    internal RedisStreamConsumer(
        RedisBusConnection connection,
        IServiceScopeFactory scopes,
        RedisStreamConsumerOptions options,
        ILogger<RedisStreamConsumer<THandler>> logger,
        IMeterFactory meterFactory,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meterFactory);
        options.Validate();

        this.connection = connection;
        this.scopes = scopes;
        this.options = options;
        this.logger = logger;
        this.time = time;
        var meter = meterFactory.Create(SmartParkingTelemetry.MeterName);
        handled = meter.CreateCounter<long>(
            "smartparking.bus.consumer.messages", "{message}", "Stream entries handled by a consumer group, by outcome.");
        groupTag = new("group", options.Group);

        // Review of Phase 3, finding 4: a handler that always throws for one entry blocks the group (by design, a
        // transient failure is never dead-lettered). These two instruments make that visible in Prometheus.
        failures = meter.CreateCounter<long>(
            "smartparking.bus.consumer.failures", "{failure}", "Failed consumer iterations (nothing acknowledged).");
        meter.CreateObservableGauge(
            "smartparking.bus.consumer.consecutive_failures",
            () => new Measurement<int>(Volatile.Read(ref consecutiveFailures), groupTag),
            "{failure}",
            "Failures since the last successfully handled batch; grows without bound while the group is blocked.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync runs ExecuteAsync synchronously up to its first await; yield so that nothing
        // below runs inside the host's start. The connection itself is opened asynchronously (RedisBusConnection),
        // so an unreachable Redis delays only this loop, not the host or /health (review of Phase 3, finding 2).
        await Task.Yield();

        var readPending = true;
        var backoff = InitialBackoff;
        var groupReady = false;
        var nextClaim = time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var redis = await connection.GetAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
                var db = redis.GetDatabase();

                if (!groupReady)
                {
                    await EnsureGroupAsync(db).ConfigureAwait(false);
                    groupReady = true;
                }

                if (time.GetUtcNow() >= nextClaim)
                {
                    if (await ClaimAbandonedAsync(db, stoppingToken).ConfigureAwait(false) > 0)
                    {
                        readPending = true;
                    }

                    nextClaim = time.GetUtcNow() + options.ClaimInterval;
                }

                var position = readPending ? StreamPosition.Beginning : StreamPosition.NewMessages;
                var entries = await db.StreamReadGroupAsync(
                    options.Stream, options.Group, options.ConsumerName, position, options.BatchSize).ConfigureAwait(false);

                if (entries.Length == 0)
                {
                    if (readPending)
                    {
                        readPending = false; // own backlog drained: switch to new entries without waiting
                        continue;
                    }

                    await Task.Delay(options.PollInterval, time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessAsync(db, entries, recovered: readPending, stoppingToken).ConfigureAwait(false);
                backoff = InitialBackoff;
                Volatile.Write(ref consecutiveFailures, 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed group creation (NOGROUP after a stream deletion, Redis down) is retried as well.
                if (ex is RedisServerException server && server.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
                {
                    groupReady = false;
                }

                LogFailure(logger, options.Stream, options.Group, backoff, ex);
                failures.Add(1, groupTag);
                Interlocked.Increment(ref consecutiveFailures);
                readPending = true;

                try
                {
                    await Task.Delay(backoff, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, options.MaxBackoff.Ticks));
            }
        }
    }

    private async Task EnsureGroupAsync(IDatabase db)
    {
        try
        {
            // From the start of the stream ("0"), so that a group created after events were published still
            // processes them; MKSTREAM so that the group can exist before the first event.
            await db.StreamCreateConsumerGroupAsync(options.Stream, options.Group, StreamPosition.Beginning, createStream: true)
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
            // Already exists: nothing to do.
        }
    }

    private async Task<int> ClaimAbandonedAsync(IDatabase db, CancellationToken cancellationToken)
    {
        var claimed = 0;
        RedisValue start = "0-0";
        do
        {
            // A large PEL left by a dead consumer takes many pages: honour a shutdown between them (finding 7).
            cancellationToken.ThrowIfCancellationRequested();
            var result = await db.StreamAutoClaimIdsOnlyAsync(
                options.Stream,
                options.Group,
                options.ConsumerName,
                (long)options.ClaimMinIdle.TotalMilliseconds,
                start,
                options.BatchSize).ConfigureAwait(false);

            claimed += result.ClaimedIds.Length;
            start = result.NextStartId;
        }
        while (start != "0-0");

        if (claimed > 0)
        {
            handled.Add(claimed, groupTag, new("outcome", "claimed"));
            LogClaimed(logger, claimed, options.Stream, options.Group, null);
        }

        return claimed;
    }

    private async Task ProcessAsync(IDatabase db, StreamEntry[] entries, bool recovered, CancellationToken cancellationToken)
    {
        var messages = new List<DeliveredMessage>(entries.Length);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (StreamFields.To(entry) is { } message)
            {
                messages.Add(new DeliveredMessage(entry.Id.ToString(), message));
            }
            else
            {
                unreadable.Add(entry.Id.ToString());
            }
        }

        using var activity = StartActivity(messages, entries.Length, recovered);

        var poison = new HashSet<string>(StringComparer.Ordinal);
        if (messages.Count > 0)
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                var outcome = await handler.HandleAsync(messages, cancellationToken).ConfigureAwait(false);
                poison.UnionWith(outcome.Poison);
            }
        }

        // Dead-letter first, then acknowledge: a crash in between redelivers the entry, which at worst is copied
        // twice to the dead-letter stream, never lost.
        poison.UnionWith(unreadable);
        var deadLetters = entries.Where(e => poison.Contains(e.Id.ToString())).ToArray();
        foreach (var entry in deadLetters)
        {
            NameValueEntry[] fields =
            [
                .. entry.IsNull ? [] : entry.Values,
                new("originalId", entry.Id),
                new("group", options.Group),
            ];
            await db.StreamAddAsync(options.DeadLetterStream, fields).ConfigureAwait(false);
        }

        var ids = entries.Select(e => e.Id).ToArray();
        await db.StreamAcknowledgeAsync(options.Stream, options.Group, ids).ConfigureAwait(false);

        var deadLettered = deadLetters.Length;
        handled.Add(ids.Length - deadLettered, groupTag, new("outcome", recovered ? "acked_recovered" : "acked"));
        if (deadLettered > 0)
        {
            handled.Add(deadLettered, groupTag, new("outcome", "dead_lettered"));
            LogDeadLettered(logger, deadLettered, options.DeadLetterStream, null);
        }
    }

    private Activity? StartActivity(List<DeliveredMessage> messages, int count, bool recovered)
    {
        // One consumer span per batch, linked to the producer spans (W3C traceparent carried in each entry).
        var links = messages
            .Select(m => m.Message.TraceParent)
            .Where(t => t is not null)
            .Distinct(StringComparer.Ordinal)
            .Select(t => ActivityContext.TryParse(t, null, out var context) ? new ActivityLink(context) : default)
            .Where(l => l.Context != default)
            .ToList();

        var activity = SmartParkingTelemetry.ActivitySource.StartActivity(
            $"consume {options.Stream}", ActivityKind.Consumer, parentContext: default, links: links);
        activity?.SetTag("messaging.system", "redis");
        activity?.SetTag("messaging.destination.name", options.Stream);
        activity?.SetTag("messaging.consumer.group.name", options.Group);
        activity?.SetTag("messaging.batch.message_count", count);
        activity?.SetTag("smartparking.recovered", recovered);
        return activity;
    }

    private static readonly Action<ILogger, string, string, TimeSpan, Exception?> LogFailure =
        LoggerMessage.Define<string, string, TimeSpan>(
            LogLevel.Warning,
            new EventId(1, "StreamConsumerFailure"),
            "Consumer of stream {Stream}, group {Group} failed; nothing acknowledged, retrying in {Backoff}.");

    private static readonly Action<ILogger, int, string, string, Exception?> LogClaimed =
        LoggerMessage.Define<int, string, string>(
            LogLevel.Information,
            new EventId(2, "StreamConsumerClaimed"),
            "Claimed {Count} abandoned entries of stream {Stream}, group {Group}.");

    private static readonly Action<ILogger, int, string, Exception?> LogDeadLettered =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(3, "StreamConsumerDeadLettered"),
            "Moved {Count} poison entries to {DeadLetterStream}.");
}
