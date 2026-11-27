using System.Globalization;
using Microsoft.Extensions.Logging;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Features.Availability;
using SmartParking.Domain.Occupancy;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>
/// Applies occupancy events to the availability view in Redis, one Lua script per event (spec 004, ronda 1). The
/// timestamp is compared and stored in Unix milliseconds, the precision of the event log, so a rebuild from MongoDB
/// restores exactly the same pair (AC-10; ADR 0010). Redis failures propagate to the caller.
/// </summary>
internal sealed partial class OccupancyProjector(
    RedisConnection redis,
    AvailabilityMetrics metrics,
    ILogger<OccupancyProjector> logger) : IOccupancyProjector
{
    public async Task<ProjectionOutcome> ApplyAsync(OccupancyEvent occupancyEvent, bool publishChanges, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occupancyEvent);

        var milliseconds = occupancyEvent.Timestamp.ToUnixTimeMilliseconds();
        var spotId = AvailabilityKeys.Id(occupancyEvent.SpotId);
        var eventId = AvailabilityKeys.Id(occupancyEvent.EventId);
        var state = occupancyEvent.Type == OccupancyEventType.Occupied ? AvailabilityKeys.Occupied : AvailabilityKeys.Free;

        var connection = await redis.GetAsync(cancellationToken);
        var result = await connection.GetDatabase().ScriptEvaluateAsync(
            AvailabilityScripts.ApplyOccupancyEvent,
            [AvailabilityKeys.Spot(occupancyEvent.SpotId)],
            [
                milliseconds,
                eventId,
                state,
                publishChanges ? 1 : 0,
                AvailabilityKeys.ChangedChannel,
                spotId,
                DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("O", CultureInfo.InvariantCulture),
                AvailabilityKeys.FacilityPrefix,
                AvailabilityKeys.ZonePrefix,
            ]).WaitAsync(cancellationToken);

        var outcome = (string?)result switch
        {
            AvailabilityMetrics.Applied => ProjectionOutcome.Applied,
            AvailabilityMetrics.Duplicate => ProjectionOutcome.Duplicate,
            AvailabilityMetrics.Stale => ProjectionOutcome.Stale,
            AvailabilityMetrics.UnknownSpot => ProjectionOutcome.UnknownSpot,
            var other => throw new InvalidOperationException($"Unexpected projector script result '{other}'."),
        };

        if (outcome == ProjectionOutcome.UnknownSpot)
        {
            LogUnknownSpot(occupancyEvent.SpotId, occupancyEvent.EventId);
        }

        metrics.EventProjected(outcome);
        return outcome;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Occupancy event {EventId} ignored: spot {SpotId} is not in the availability view")]
    private partial void LogUnknownSpot(Guid spotId, Guid eventId);
}
