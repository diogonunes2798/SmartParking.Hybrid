using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Features.Ingestion;

/// <summary>
/// Ingests a batch of occupancy events from a facility gateway (UC-01, RF-01, spec 003). The schema was checked at
/// the API boundary; here each event is checked against the sensor registry (one query per batch), and the accepted
/// ones are appended to the event log (one call per batch) before the answer, so the gateway is only confirmed after a
/// durable write (D3, RNF-03). The <c>eventId</c> alone decides idempotency: the first one written wins; within a batch
/// the first in request order wins. A registry inconsistency rejects only its event; an unreachable store fails the
/// whole batch (503), and repeating it is safe.
/// </summary>
public static class IngestOccupancyBatch
{
    public const int MaxEvents = 500;

    public const string DuplicateStatus = "duplicate";
    public const string RejectedStatus = "rejected";

    public sealed record Item(
        Guid EventId,
        string HardwareId,
        Guid SpotId,
        OccupancyEventType Type,
        DateTimeOffset Timestamp,
        string? Payload);

    public sealed record Command(IReadOnlyList<Item> Events);

    /// <summary>An event that was not accepted, by its position in the request.</summary>
    public sealed record Issue(int Index, Guid EventId, string Status, string? Code);

    /// <summary><see cref="Issues"/> lists only the events not accepted, in request order.</summary>
    public sealed record Outcome(int Accepted, int Duplicates, int Rejected, IReadOnlyList<Issue> Issues);

    internal sealed class Handler(
        ISensorRegistryReader registry,
        IOccupancyEventLog eventLog,
        IngestionPolicy policy,
        IngestionMetrics metrics,
        TimeProvider clock) : ICommandHandler<Command, Outcome>
    {
        public async Task<Result<Outcome>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var receivedAt = clock.GetUtcNow();
            var issues = new SortedList<int, Issue>();

            // An unordered insertMany does not keep the request order, so repeats inside the batch are removed first (AC-5).
            HashSet<Guid> seen = [];
            List<(int Index, Item Item)> candidates = [];
            for (var index = 0; index < command.Events.Count; index++)
            {
                var item = command.Events[index];
                if (seen.Add(item.EventId))
                {
                    candidates.Add((index, item));
                }
                else
                {
                    issues.Add(index, new Issue(index, item.EventId, DuplicateStatus, null));
                }
            }

            var started = clock.GetTimestamp();
            var lookup = await registry.GetByHardwareIdsAsync(
                [.. candidates.Select(entry => entry.Item.HardwareId).Distinct(StringComparer.Ordinal)],
                cancellationToken);
            metrics.ValidationCompleted(clock.GetElapsedTime(started));
            if (lookup.Error is { } registryError)
            {
                return registryError;
            }

            var sensors = lookup.Value.ToDictionary(sensor => sensor.HardwareId, StringComparer.Ordinal);
            var latestAccepted = receivedAt + policy.FutureTolerance;
            List<(int Index, OccupancyEvent Event)> accepted = [];
            foreach (var (index, item) in candidates)
            {
                var sensor = sensors.GetValueOrDefault(item.HardwareId);
                if (RejectionCode(item, sensor, latestAccepted) is { } code)
                {
                    issues.Add(index, new Issue(index, item.EventId, RejectedStatus, code));
                    continue;
                }

                accepted.Add((index, new OccupancyEvent(
                    item.EventId,
                    sensor!.SensorId,
                    sensor.HardwareId,
                    item.SpotId,
                    sensor.FacilityId,
                    sensor.ZoneId!.Value,
                    item.Type,
                    item.Timestamp,
                    receivedAt,
                    item.Payload)));
            }

            var rejected = issues.Count - (command.Events.Count - candidates.Count);
            var alreadyLogged = 0;
            if (accepted.Count > 0)
            {
                started = clock.GetTimestamp();
                var append = await eventLog.AppendAsync([.. accepted.Select(entry => entry.Event)], cancellationToken);
                metrics.AppendCompleted(clock.GetElapsedTime(started));
                if (append.Error is { } logError)
                {
                    return logError;
                }

                foreach (var (index, occupancyEvent) in accepted.Where(entry => append.Value.Duplicates.Contains(entry.Event.EventId)))
                {
                    issues.Add(index, new Issue(index, occupancyEvent.EventId, DuplicateStatus, null));
                    alreadyLogged++;
                }
            }

            var acceptedCount = accepted.Count - alreadyLogged;
            var duplicates = command.Events.Count - candidates.Count + alreadyLogged;
            var outcome = new Outcome(acceptedCount, duplicates, rejected, [.. issues.Values]);
            RecordOutcome(command.Events.Count, outcome);
            return outcome;
        }

        /// <summary>
        /// Outcome counters and the batch size are recorded only for a batch that is answered with 200. A batch that
        /// ends in 503 is sent again by the gateway, and counting it twice would distort the F12 measurements
        /// (review of F4a, finding 3). The durations are recorded on every attempt, because they measure the stores.
        /// </summary>
        private void RecordOutcome(int batchSize, Outcome outcome)
        {
            metrics.BatchReceived(batchSize);
            metrics.EventsAccepted(outcome.Accepted);
            metrics.EventsDuplicated(outcome.Duplicates);
            foreach (var issue in outcome.Issues.Where(issue => issue.Code is not null))
            {
                metrics.EventRejected(issue.Code!);
            }
        }

        /// <summary>Rules 1 to 8, in the spec's order: the first that fails gives the code (AC-8).</summary>
        private static string? RejectionCode(Item item, SensorRegistration? sensor, DateTimeOffset latestAccepted) => sensor switch
        {
            null => IngestionCodes.SensorUnknown,
            { Status: not SensorStatus.Active } => IngestionCodes.SensorInactive,
            { Kind: not SensorKind.Occupancy } => IngestionCodes.SensorNotOccupancy,
            { SpotId: null } => IngestionCodes.SensorUnassigned,
            { SpotId: var spotId } when spotId != item.SpotId => IngestionCodes.SpotMismatch,
            { SpotStatus: not SpotStatus.Active } => IngestionCodes.SpotInactive,
            { FacilityStatus: not FacilityStatus.Active } => IngestionCodes.FacilityInactive,
            _ when item.Timestamp > latestAccepted => IngestionCodes.TimestampInFuture,
            _ => null,
        };
    }
}
