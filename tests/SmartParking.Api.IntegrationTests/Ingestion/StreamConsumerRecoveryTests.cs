using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Ingestion;

namespace SmartParking.Api.IntegrationTests.Ingestion;

/// <summary>
/// At-least-once delivery (D2, ADR-0006) with an idempotent log (D3, ADR-0007). Each test uses its own stream and
/// group, published through <see cref="IEventBus"/> directly, so that pending entries never leak between tests.
/// </summary>
[Collection(IngestionTestGroup.Name)]
public sealed class StreamConsumerRecoveryTests(IngestionFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The consumer writes to MongoDB and is stopped before XACK. On restart (same consumer name) it re-reads its
    /// own pending entry, writes again (a duplicate key, i.e. a no-op) and acknowledges: one document, nothing pending.
    /// </summary>
    [Fact]
    public async Task Consumer_stopped_before_ack_reprocesses_on_restart_without_duplicating()
    {
        var (stream, group) = NewStream();
        var eventId = Guid.NewGuid();
        var gate = new StopBeforeAckGate();

        var crashing = await fixture.StartConsumerAsync<StopBeforeAckHandler>(
            o => Configure(o, stream, group, "worker-1"),
            s => s.AddSingleton(gate));
        await PublishAsync(stream, eventId);

        await gate.Written.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(1, await fixture.CountAsync(eventId));
        Assert.Equal(1, await PendingAsync(stream, group));

        await crashing.StopAsync(); // cancels the handler: no XACK
        Assert.Equal(1, await PendingAsync(stream, group));

        await using var restarted = await fixture.StartConsumerAsync<OccupancyLogWriter>(o => Configure(o, stream, group, "worker-1"));
        await IngestionFixture.EventuallyAsync(async () => await PendingAsync(stream, group) == 0, "pending entry acknowledged");

        Assert.Equal(1, await fixture.CountAsync(eventId));
    }

    /// <summary>The consumer that held the entry never comes back: another consumer claims it (XAUTOCLAIM).</summary>
    [Fact]
    public async Task Entry_abandoned_by_a_dead_consumer_is_claimed_by_another()
    {
        var (stream, group) = NewStream();
        var eventId = Guid.NewGuid();
        var gate = new StopBeforeAckGate();

        var crashing = await fixture.StartConsumerAsync<StopBeforeAckHandler>(
            o => Configure(o, stream, group, "worker-dead"),
            s => s.AddSingleton(gate));
        await PublishAsync(stream, eventId);
        await gate.Written.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await crashing.StopAsync();

        await using var survivor = await fixture.StartConsumerAsync<OccupancyLogWriter>(o =>
        {
            Configure(o, stream, group, "worker-alive");
            o.ClaimInterval = TimeSpan.FromMilliseconds(100);
            o.ClaimMinIdle = TimeSpan.FromMilliseconds(300);
        });
        await IngestionFixture.EventuallyAsync(async () => await PendingAsync(stream, group) == 0, "abandoned entry claimed and acknowledged");

        Assert.Equal(1, await fixture.CountAsync(eventId));
        var consumers = await fixture.RedisConnection.GetDatabase().StreamConsumerInfoAsync(stream, group);
        Assert.Equal(0, consumers.Single(c => c.Name == "worker-dead").PendingMessageCount);
    }

    [Fact]
    public async Task Malformed_entry_goes_to_the_dead_letter_stream_and_does_not_block_the_next_ones()
    {
        var (stream, group) = NewStream();
        var eventId = Guid.NewGuid();
        var db = fixture.RedisConnection.GetDatabase();
        await db.StreamAddAsync(stream, [new("key", "garbage"), new("body", "{not json")]);
        await PublishAsync(stream, eventId);

        await using var consumer = await fixture.StartConsumerAsync<OccupancyLogWriter>(o => Configure(o, stream, group, "worker-1"));
        await IngestionFixture.EventuallyAsync(async () => await fixture.CountAsync(eventId) == 1, "valid entry stored");
        await IngestionFixture.EventuallyAsync(async () => await PendingAsync(stream, group) == 0, "both entries acknowledged");

        var dead = await db.StreamRangeAsync(stream + ":dead");
        var entry = Assert.Single(dead);
        Assert.Equal("{not json", entry["body"].ToString());
        Assert.Equal(group, entry["group"].ToString());
    }

    [Fact]
    public async Task Appending_the_same_record_twice_reports_a_duplicate_not_an_error()
    {
        var log = fixture.Factory.Services.GetRequiredService<IOccupancyEventLog>();
        var record = new OccupancyEventRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Domain.Occupancy.OccupancyEventType.Freed, DateTimeOffset.UtcNow, "{}", DateTimeOffset.UtcNow);

        var first = await log.AppendAsync([record], Ct);
        var second = await log.AppendAsync([record, record with { EventId = Guid.NewGuid() }], Ct);

        Assert.Equal((1, 0), (first.Inserted, first.Duplicates));
        Assert.Equal((1, 1), (second.Inserted, second.Duplicates));
        Assert.Empty(second.Refused);
        Assert.Equal(1, await fixture.CountAsync(record.EventId));
    }

    private static (string Stream, string Group) NewStream()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"test:occupancy:{suffix}", "test-group");
    }

    private static void Configure(Infrastructure.Messaging.RedisStreamConsumerOptions o, string stream, string group, string consumer)
    {
        o.Stream = stream;
        o.Group = group;
        o.ConsumerName = consumer;
        o.PollInterval = TimeSpan.FromMilliseconds(20);
    }

    private async Task PublishAsync(string stream, Guid eventId)
    {
        var bus = fixture.Factory.Services.GetRequiredService<IEventBus>();
        var message = new OccupancyEventMessage(
            eventId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), OccupancyEventMessage.Occupied,
            DateTimeOffset.UtcNow, System.Text.Json.JsonDocument.Parse("{}").RootElement, DateTimeOffset.UtcNow);
        await bus.PublishAsync(stream, [new BusMessage(eventId.ToString(), message.ToJson())], Ct);
    }

    private async Task<long> PendingAsync(string stream, string group) =>
        (await fixture.RedisConnection.GetDatabase().StreamPendingAsync(stream, group)).PendingMessageCount;

    /// <summary>Signals when the batch is in MongoDB; the handler then never returns (until cancelled).</summary>
    public sealed class StopBeforeAckGate
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Writes through the real <see cref="OccupancyLogWriter"/>, then hangs: the process "stops" before XACK.</summary>
    public sealed class StopBeforeAckHandler(OccupancyLogWriter inner, StopBeforeAckGate gate) : IMessageBatchHandler
    {
        public async Task<BatchOutcome> HandleAsync(IReadOnlyList<DeliveredMessage> messages, CancellationToken cancellationToken)
        {
            await inner.HandleAsync(messages, cancellationToken);
            gate.Written.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new System.Diagnostics.UnreachableException();
        }
    }
}
