using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Abstractions.Availability;

/// <summary>
/// Applies one occupancy event to the availability view (spec 004, UC-03, RF-03). Idempotent and order-tolerant: an
/// event is applied only when its (timestamp, eventId) pair is newer than the last one applied to the spot. Store
/// failures propagate to the caller, which decides the retry (the transport arrives in F6; D2).
/// </summary>
public interface IOccupancyProjector
{
    /// <param name="occupancyEvent">The event; only its spot, type, timestamp and eventId are used.</param>
    /// <param name="publishChanges">Publish a message on a state transition (false during a rebuild).</param>
    /// <param name="cancellationToken">Cancels the wait for the store.</param>
    Task<ProjectionOutcome> ApplyAsync(OccupancyEvent occupancyEvent, bool publishChanges, CancellationToken cancellationToken);
}
