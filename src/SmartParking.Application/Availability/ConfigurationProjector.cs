using System.Diagnostics;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Diagnostics;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Availability;

/// <summary>
/// RF-06 → read model: consumes the configuration events (outbox rows relayed to
/// <see cref="ConfigurationEventMessage.Topic"/>, Phase 5) and creates, updates or removes facilities and spots in
/// the read model. Each event carries the full state, and the read model keeps per facility/spot the time of the
/// last change applied, so a redelivered or late event is ignored (idempotent, ADR-0008). Event types the read
/// model does not need (e.g. sessions, if the relay ever shares the stream) are acknowledged and ignored.
/// </summary>
/// <remarks>
/// Phase 5 (ADR-0009): for every event applied now, records the consistency window (outbox <c>occurred_at</c> → read
/// model updated, Cap. 4.5) and continues the trace of the request that raised it (one span per event, child of the
/// relay's span carried in the message).
/// </remarks>
public sealed class ConfigurationProjector(
    IAvailabilityReadModel readModel,
    ReadModelGate gate,
    ReadModelMetrics metrics,
    TimeProvider time) : IMessageBatchHandler
{
    public async Task<BatchOutcome> HandleAsync(IReadOnlyList<DeliveredMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var poison = new List<string>();

        // In order, one at a time: a spot's event may depend on its facility's (zone of the counters), and the
        // relay publishes in outbox order.
        foreach (var delivered in messages)
        {
            if (ConfigurationEventMessage.TryParse(delivered.Message.Body) is not { } message)
            {
                poison.Add(delivered.Id);
                continue;
            }

            ActivityContext.TryParse(delivered.Message.TraceParent, null, out var parent);
            using var span = SmartParkingTelemetry.ActivitySource.StartActivity(
                "apply configuration event", ActivityKind.Consumer, parent);
            span?.SetTag("messaging.message.id", message.Id.ToString());
            span?.SetTag("smartparking.outbox.type", message.Type);

            var changedAt = message.OccurredAt;
            ConfigurationOutcome? outcome = message.Type switch
            {
                nameof(FacilityChanged) => message.PayloadAs<FacilityChanged>() is { } e && IsValid(e)
                    ? await readModel.UpsertFacilityAsync(
                        new FacilitySnapshot(e.FacilityId, e.Name, e.Latitude, e.Longitude, e.Capacity, e.Type, e.Zone),
                        changedAt,
                        cancellationToken).ConfigureAwait(false)
                    : null,
                nameof(FacilityDeleted) => message.PayloadAs<FacilityDeleted>() is { } e
                    ? await readModel.DeleteFacilityAsync(e.FacilityId, changedAt, cancellationToken).ConfigureAwait(false)
                    : null,
                nameof(SpotChanged) => message.PayloadAs<SpotChanged>() is { } e && IsValid(e)
                    ? await readModel.UpsertSpotAsync(
                        new SpotSnapshot(e.SpotId, e.FacilityId, e.AllowedVehicleType), changedAt, cancellationToken).ConfigureAwait(false)
                    : null,
                nameof(SpotDeleted) => message.PayloadAs<SpotDeleted>() is { } e
                    ? await readModel.DeleteSpotAsync(e.SpotId, changedAt, cancellationToken).ConfigureAwait(false)
                    : null,
                _ => ConfigurationOutcome.Ignored,
            };

            if (outcome is null)
            {
                // A known type whose payload does not deserialise or breaks a domain rule: never processable. Checked
                // here because a Lua script that fails half way keeps its earlier writes (review of Phase 4, finding 7).
                poison.Add(delivered.Id);
                span?.SetStatus(ActivityStatusCode.Error, "poison");
                continue;
            }

            if (outcome == ConfigurationOutcome.Applied)
            {
                // Only when the read model changed now: a redelivery (stale) would add the time until the redelivery.
                metrics.ConsistencyWindow(message.Type, time.GetUtcNow() - message.OccurredAt);
            }

            span?.SetTag("smartparking.outcome", outcome.Value.ToString());
            metrics.Configuration(message.Type, outcome.Value switch
            {
                ConfigurationOutcome.Applied => "applied",
                ConfigurationOutcome.Stale => "stale",
                ConfigurationOutcome.Unknown => "unknown",
                _ => "ignored",
            });
        }

        metrics.Poison(poison.Count);
        return poison.Count == 0 ? BatchOutcome.AllProcessed : new BatchOutcome(poison);
    }

    /// <summary>The same rules the domain applies when the facility is saved (RF-06, ADR-0003).</summary>
    private static bool IsValid(FacilityChanged e)
    {
        if (e.FacilityId == Guid.Empty || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Zone)
            || e.Capacity <= 0 || !Enum.IsDefined(e.Type))
        {
            return false;
        }

        try
        {
            _ = new GeoPoint(e.Latitude, e.Longitude); // Redis GEO limits
            return true;
        }
        catch (DomainValidationException)
        {
            return false;
        }
    }

    private static bool IsValid(SpotChanged e) =>
        e.SpotId != Guid.Empty && e.FacilityId != Guid.Empty && Enum.IsDefined(e.AllowedVehicleType);
}
