using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SmartParking.SensorSimulator.Generation;
using SmartParking.SensorSimulator.Provisioning;

namespace SmartParking.SensorSimulator.Sending;

/// <summary>
/// An emitting run in real time (spec 003): the generated events are released when the clock reaches their timestamp,
/// one virtual gateway per facility of the mapping batches them (<see cref="BatchAccumulator"/>), and each gateway
/// sends its batches one at a time, in order, on its own task (<see cref="IngestionSender"/>), so a 503 wait does not
/// stop the generation. The run ends at <c>duration</c> or on cancellation; the buffered events are flushed and the
/// pending sends get <see cref="DrainTimeout"/> to finish.
/// </summary>
internal sealed partial class GatewayRunner(IngestionSender sender, TimeProvider clock, ILogger<GatewayRunner> logger)
{
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    public async Task<RunSummary> RunAsync(
        SensorMap map,
        GeneratorSettings generator,
        SenderSettings settings,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(settings);

        var start = OccupancyGenerator.TruncateToMilliseconds(clock.GetUtcNow());
        var end = duration is { } length ? start + length : DateTimeOffset.MaxValue;
        LogStarted(logger, map.Sensors.Count, start, generator.Seed);

        using var drain = new CancellationTokenSource();
        var gateways = map.Sensors
            .Select(sensor => sensor.FacilityId)
            .Distinct()
            .ToDictionary(facilityId => facilityId, _ => new Gateway(settings, sender, drain.Token));

        long generated = 0;
        using var events = OccupancyGenerator.Generate(map.Sensors, generator, start).GetEnumerator();
        var pending = events.MoveNext() ? events.Current : null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                while (pending is not null && pending.Timestamp <= now && pending.Timestamp < end)
                {
                    generated++;
                    gateways[pending.FacilityId].Add(pending, now);
                    pending = events.MoveNext() ? events.Current : null;
                }

                foreach (var gateway in gateways.Values)
                {
                    gateway.FlushIfDue(now);
                }

                if (now >= end || pending is null)
                {
                    break;
                }

                var wake = Min(pending.Timestamp, end, gateways.Values.Select(gateway => gateway.DueAt));
                if (wake > now)
                {
                    await Task.Delay(wake - now, clock, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C: stop generating and flush what is buffered below.
        }

        drain.CancelAfter(DrainTimeout);
        var outcomes = new List<SendOutcome>();
        foreach (var gateway in gateways.Values)
        {
            outcomes.AddRange(await gateway.CompleteAsync());
        }

        // Also counts the batches never sent because the drain timeout passed.
        var notDelivered = generated - outcomes.Where(outcome => outcome.Delivered).Sum(outcome => (long)outcome.Events);
        var summary = new RunSummary(
            start,
            generated,
            outcomes.Count,
            outcomes.Sum(outcome => (long)outcome.Accepted),
            outcomes.Sum(outcome => (long)outcome.Duplicates),
            outcomes.Sum(outcome => (long)outcome.Rejected),
            outcomes.Sum(outcome => (long)outcome.Retries),
            notDelivered);

        LogFinished(logger, summary.Generated, summary.Batches, summary.Accepted, summary.Duplicates, summary.Rejected,
            summary.Retries, summary.NotDeliveredEvents);
        return summary;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second, IEnumerable<DateTimeOffset?> others)
    {
        var min = first < second ? first : second;
        foreach (var other in others)
        {
            if (other is { } value && value < min)
            {
                min = value;
            }
        }

        return min;
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Information,
        Message = "Emitting for {Spots} spots from {Start:O} with seed {Seed}")]
    private static partial void LogStarted(ILogger logger, int spots, DateTimeOffset start, int seed);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "Run finished: {Generated} events in {Batches} batches; accepted {Accepted}, duplicates {Duplicates}, rejected {Rejected}, retries {Retries}, not delivered {NotDelivered}")]
    private static partial void LogFinished(
        ILogger logger, long generated, long batches, long accepted, long duplicates, long rejected, long retries, long notDelivered);

    /// <summary>One virtual gateway: its accumulator and a single consumer that sends the batches in order.</summary>
    private sealed class Gateway
    {
        private readonly BatchAccumulator accumulator;

        private readonly Channel<IReadOnlyList<SimulatedEvent>> batches = Channel.CreateUnbounded<IReadOnlyList<SimulatedEvent>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        private readonly Task<List<SendOutcome>> consumer;

        public Gateway(SenderSettings settings, IngestionSender sender, CancellationToken drainToken)
        {
            accumulator = new BatchAccumulator(settings.BatchSize, settings.FlushInterval);
            consumer = ConsumeAsync(sender, settings.MaxRetries, drainToken);
        }

        public DateTimeOffset? DueAt => accumulator.DueAt;

        public void Add(SimulatedEvent simulatedEvent, DateTimeOffset now) => Enqueue(accumulator.Add(simulatedEvent, now));

        public void FlushIfDue(DateTimeOffset now) => Enqueue(accumulator.TakeDue(now));

        public Task<List<SendOutcome>> CompleteAsync()
        {
            Enqueue(accumulator.TakeAll());
            batches.Writer.Complete();
            return consumer;
        }

        private void Enqueue(IReadOnlyList<SimulatedEvent>? batch)
        {
            if (batch is not null)
            {
                batches.Writer.TryWrite(batch);
            }
        }

        private async Task<List<SendOutcome>> ConsumeAsync(IngestionSender sender, int maxRetries, CancellationToken drainToken)
        {
            var outcomes = new List<SendOutcome>();
            try
            {
                await foreach (var batch in batches.Reader.ReadAllAsync(drainToken))
                {
                    outcomes.Add(await sender.SendAsync(batch, maxRetries, drainToken));
                }
            }
            catch (OperationCanceledException) when (drainToken.IsCancellationRequested)
            {
                // The drain timeout passed after the end of the run: the remaining batches are not sent.
            }

            return outcomes;
        }
    }
}
