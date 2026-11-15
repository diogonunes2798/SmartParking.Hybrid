using SmartParking.Application.Abstractions;
using SmartParking.Application.Ingestion;

namespace SmartParking.Application.Availability;

/// <summary>Identity of the consumer groups that feed the availability read model (UC-01 step 4, RF-06).</summary>
public static class AvailabilityConsumers
{
    /// <summary>Group on the occupancy stream (the second group, next to <see cref="OccupancyLogConsumer.Name"/>).</summary>
    public const string Occupancy = "availability";

    /// <summary>
    /// Configuration name and group on the configuration stream. A name of its own, so that the bus metrics of the two
    /// projectors are not mixed and a blocked one is not hidden by the other (review of Phase 4, finding 6).
    /// </summary>
    public const string Configuration = "availability-configuration";
}

/// <summary>
/// UC-01 step 4: second consumer group on the occupancy stream (ADR-0006). Applies each event to the read model
/// through <see cref="IAvailabilityReadModel.ApplyAsync"/>, which is atomic per event and keeps only the newest event
/// of each spot, so the projector is idempotent and tolerates out-of-order delivery (ADR-0008). It returns only after
/// Redis has applied the whole batch: the bus acknowledges nothing that is not in the read model.
/// </summary>
public sealed class AvailabilityProjector(
    IAvailabilityReadModel readModel,
    ReadModelGate gate,
    ReadModelMetrics metrics,
    TimeProvider time) : IMessageBatchHandler
{
    public async Task<BatchOutcome> HandleAsync(IReadOnlyList<DeliveredMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var parsed = new List<OccupancyEventMessage>(messages.Count);
        var poison = new List<string>();
        foreach (var delivered in messages)
        {
            if (OccupancyEventMessage.TryParse(delivered.Message.Body) is { } m)
            {
                parsed.Add(m);
            }
            else
            {
                poison.Add(delivered.Id);
            }
        }

        if (parsed.Count > 0)
        {
            var changes = parsed.Select(m => new OccupancyChange(
                    m.EventId,
                    m.SpotId,
                    m.Type == OccupancyEventMessage.Occupied ? SpotState.Occupied : SpotState.Free,
                    m.Timestamp.ToUnixTimeMilliseconds()))
                .ToList();

            var outcomes = await readModel.ApplyAsync(changes, cancellationToken).ConfigureAwait(false);

            var now = time.GetUtcNow();
            for (var i = 0; i < outcomes.Count; i++)
            {
                metrics.Handled(outcomes[i]);
                if (outcomes[i] is ApplyOutcome.Applied or ApplyOutcome.Unchanged)
                {
                    metrics.Lag(now - parsed[i].Timestamp, now - parsed[i].ReceivedAt);
                }
            }
        }

        metrics.Poison(poison.Count);
        return poison.Count == 0 ? BatchOutcome.AllProcessed : new BatchOutcome(poison);
    }
}
