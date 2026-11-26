using System.Net.Http.Headers;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.SensorSimulator.Generation;
using SmartParking.SensorSimulator.Sending;

namespace SmartParking.Api.Tests.Features.Simulator;

/// <summary>
/// The batching rule of a virtual gateway on the fake clock (spec 003, AC-23): a batch leaves at 500 events or after
/// 1 s, whichever comes first. Pure: no Api and no database.
/// </summary>
public sealed class BatchAccumulatorTests
{
    private static readonly SenderSettings Defaults = SenderSettings.Default;

    [Fact]
    public void Accumulator_500Events_SendsFullBatch_AC23()
    {
        // Arrange
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));
        var accumulator = new BatchAccumulator(Defaults.BatchSize, Defaults.FlushInterval);
        var events = Events(Defaults.BatchSize + 1);

        // Act
        var early = events.Take(Defaults.BatchSize - 1).Select(item => accumulator.Add(item, clock.GetUtcNow())).ToList();
        var full = accumulator.Add(events[Defaults.BatchSize - 1], clock.GetUtcNow());
        var next = accumulator.Add(events[Defaults.BatchSize], clock.GetUtcNow());

        // Assert
        Assert.All(early, Assert.Null);
        Assert.NotNull(full);
        Assert.Equal(events.Take(Defaults.BatchSize), full);
        Assert.Null(next);
        Assert.Equal(1, accumulator.Count);
    }

    [Fact]
    public void Accumulator_OneSecondElapsed_SendsPartialBatch_AC23()
    {
        // Arrange
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));
        var accumulator = new BatchAccumulator(Defaults.BatchSize, Defaults.FlushInterval);
        var events = Events(3);

        // Act: the 1 s counts from the oldest buffered event, not from the latest.
        accumulator.Add(events[0], clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(400));
        accumulator.Add(events[1], clock.GetUtcNow());
        accumulator.Add(events[2], clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(599));
        var beforeOneSecond = accumulator.TakeDue(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var atOneSecond = accumulator.TakeDue(clock.GetUtcNow());
        var afterFlush = accumulator.TakeDue(clock.GetUtcNow().AddHours(1));

        // Assert
        Assert.Null(beforeOneSecond);
        Assert.Equal(events, atOneSecond);
        Assert.Null(afterFlush);
        Assert.Null(accumulator.DueAt);
    }

    [Fact]
    public void Accumulator_ConfiguredSizeAndInterval_AreHonoured_AC23()
    {
        // Arrange: both values are configurable (spec 003, round 6).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));
        var accumulator = new BatchAccumulator(3, TimeSpan.FromMilliseconds(250));
        var events = Events(5);

        // Act
        var batches = events.Select(item => accumulator.Add(item, clock.GetUtcNow())).Where(batch => batch is not null).ToList();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var due = accumulator.TakeDue(clock.GetUtcNow());

        // Assert
        Assert.Equal([3], batches.Select(batch => batch!.Count));
        Assert.Equal(events.Skip(3), due);
    }

    [Fact]
    public void RetryAfter_SecondsDateOrMissing_GivesTheWaitToApply_AC24()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

        // Act
        var seconds = IngestionSender.RetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)), now);
        var date = IngestionSender.RetryAfter(new RetryConditionHeaderValue(now.AddSeconds(3)), now);
        var pastDate = IngestionSender.RetryAfter(new RetryConditionHeaderValue(now.AddSeconds(-3)), now);
        var missing = IngestionSender.RetryAfter(null, now);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(2), seconds);
        Assert.Equal(TimeSpan.FromSeconds(3), date);
        Assert.Equal(TimeSpan.Zero, pastDate);
        Assert.Equal(IngestionSender.DefaultRetryAfter, missing);
    }

    private static List<SimulatedEvent> Events(int count)
    {
        var settings = new GeneratorSettings(42, TimeSpan.FromSeconds(1), 10, []);
        return [.. OccupancyGenerator.Generate(SimulatorMaps.Synthetic(10), settings, new DateTimeOffset(2026, 12, 2, 9, 0, 0, TimeSpan.Zero)).Take(count)];
    }
}
