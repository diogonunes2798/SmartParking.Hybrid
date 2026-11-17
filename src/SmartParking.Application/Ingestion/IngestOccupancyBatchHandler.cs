using System.Diagnostics;
using System.Text.Json;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Diagnostics;

namespace SmartParking.Application.Ingestion;

/// <summary>Settings of the ingestion endpoint (configuration section <c>Ingestion</c>).</summary>
public sealed class IngestionSettings
{
    /// <summary>Maximum events per request. Larger batches are rejected with 400.</summary>
    public int MaxBatchSize { get; set; } = 1000;

    /// <summary>
    /// Behaviour when a batch mixes valid and invalid events. The specification (§4, UC-01) does not define it.
    /// Default <c>false</c>: publish the valid ones and report the invalid ones (partial acceptance).
    /// <c>true</c>: reject the whole batch with 400 if any event is invalid.
    /// TODO(Diogo): confirm with the supervisor which behaviour the thesis describes.
    /// </summary>
    public bool RejectWholeBatchOnInvalid { get; set; }

    /// <summary>
    /// How far in the future (relative to the API's clock) a sensor <c>timestamp</c> may be. A later timestamp is
    /// rejected: the read model keeps only the newest event of a spot, so one event dated in the future would freeze
    /// the spot until that date, and a rebuild would pick it again (review of Phase 4, cache finding 3).
    /// Default 5 minutes. TODO(Diogo): confirm the tolerance for sensor clock drift (Cap. 3).
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far in the past a sensor <c>timestamp</c> may be (Phase 10, ADR-0013). An older event would arrive after its
    /// day was archived, or behind the retention purge horizon, and be removed without ever being aggregated or archived.
    /// Must be at most <c>Retention:ArchiveDelay</c> (Workers). Default 7 days. TODO(Diogo): confirm (Cap. 3).
    /// </summary>
    public TimeSpan MaxEventAge { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>An event of the batch that failed RF-01 validation, by position in the request array.</summary>
public sealed record RejectedEvent(int Index, Guid? EventId, IReadOnlyList<string> Errors);

/// <summary>Body of the 202 response.</summary>
public sealed record IngestAccepted(int Accepted, int DuplicatesInBatch, IReadOnlyList<RejectedEvent> Rejected);

/// <summary>Outcome of a batch: either accepted (202, possibly partial) or invalid as a whole (400).</summary>
public sealed record IngestOutcome(IngestAccepted? Accepted, string? Error, IReadOnlyList<RejectedEvent> Rejected)
{
    public bool IsAccepted => Accepted is not null;
}

/// <summary>
/// UC-01 steps 1–2: validates the minimal schema (RF-01), removes duplicate <c>eventId</c>s within the batch and
/// publishes the valid events on the bus (D2). The API answers 202 once the bus has them; it does not wait for the
/// log or the read model. Duplicates <em>across</em> requests are not filtered here: the final idempotency
/// guarantee is the unique index on <c>eventId</c> in the event log (ADR-0007), because the bus delivers at least once.
/// </summary>
public sealed class IngestOccupancyBatchHandler(
    IEventBus bus,
    IngestionSettings settings,
    OccupancyIngestionMetrics metrics,
    TimeProvider time)
{
    public async Task<IngestOutcome> HandleAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Array)
        {
            return Invalid("The body must be a JSON array of occupancy events.");
        }

        var count = body.GetArrayLength();
        if (count == 0)
        {
            return Invalid("The batch is empty.");
        }

        if (count > settings.MaxBatchSize)
        {
            return Invalid($"The batch has {count} events; the maximum is {settings.MaxBatchSize}.");
        }

        using var activity = SmartParkingTelemetry.ActivitySource.StartActivity("ingest occupancy batch");
        activity?.SetTag("smartparking.batch.size", count);

        var receivedAt = time.GetUtcNow();
        var traceParent = Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null;
        var seen = new HashSet<Guid>();
        var messages = new List<BusMessage>(count);
        var rejected = new List<RejectedEvent>();
        var duplicates = 0;
        var index = 0;

        foreach (var item in body.EnumerateArray())
        {
            var message = OccupancyEventValidator.Validate(item, receivedAt, settings.MaxClockSkew, settings.MaxEventAge, out var errors);
            if (message is null)
            {
                rejected.Add(new RejectedEvent(index, OccupancyEventValidator.TryReadEventId(item), errors));
            }
            else if (!seen.Add(message.EventId))
            {
                duplicates++;
            }
            else
            {
                messages.Add(new BusMessage(message.EventId.ToString(), message.ToJson(), traceParent));
            }

            index++;
        }

        metrics.Received(messages.Count, rejected.Count, duplicates);

        if (messages.Count == 0 || (rejected.Count > 0 && settings.RejectWholeBatchOnInvalid))
        {
            return new IngestOutcome(null, "No event of the batch was accepted.", rejected);
        }

        await bus.PublishAsync(OccupancyEventMessage.Topic, messages, cancellationToken).ConfigureAwait(false);

        return new IngestOutcome(new IngestAccepted(messages.Count, duplicates, rejected), null, rejected);
    }

    private static IngestOutcome Invalid(string error) => new(null, error, []);
}
