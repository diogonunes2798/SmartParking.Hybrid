using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Availability;

/// <summary>
/// Rebuilds the availability view from the sources of truth (spec 004, «Rebuild»; RNF-03): clears the <c>avail:</c>
/// keys, writes the layout of the active configuration read from PostgreSQL with every spot free, then applies the last
/// event of each spot read from MongoDB through the same projector, without publishing. Runs offline, with the projector
/// stopped (nothing invokes it until F6). A store failure propagates to the caller: the rebuild is idempotent, so the
/// answer to a failure is to run it again.
/// </summary>
public static class RebuildAvailability
{
    /// <summary>Concurrent projector calls while replaying the last events; each call is one Lua script.</summary>
    public const int ReplayParallelism = 32;

    public sealed record Command;

    public sealed record Summary(
        int Facilities,
        int Zones,
        int Spots,
        int EventsRead,
        int Applied,
        int Duplicates,
        int Stale,
        int UnknownSpots,
        TimeSpan Duration);

    internal sealed class Handler(
        IOccupancyEventIndexes indexes,
        IAvailabilityViewStore view,
        IAvailabilityLayoutReader layoutReader,
        ILatestOccupancyEventReader latestEvents,
        IOccupancyProjector projector,
        AvailabilityMetrics metrics,
        TimeProvider clock) : ICommandHandler<Command, Summary>
    {
        public async Task<Result<Summary>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var started = clock.GetTimestamp();

            // The replay below reads through this index; creating it here makes the rebuild work on a fresh log too.
            await indexes.EnsureAsync(cancellationToken);
            await view.ClearAsync(cancellationToken);

            var layout = await layoutReader.ReadAsync(cancellationToken);
            await view.SeedAsync(layout, cancellationToken);

            var counts = new int[Enum.GetValues<ProjectionOutcome>().Length];
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = ReplayParallelism, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(latestEvents.ReadLatestPerSpotAsync(cancellationToken), parallel, async (occupancyEvent, ct) =>
            {
                var outcome = await projector.ApplyAsync(occupancyEvent, publishChanges: false, ct);
                Interlocked.Increment(ref counts[(int)outcome]);
            });

            var elapsed = clock.GetElapsedTime(started);
            metrics.RebuildCompleted(elapsed);

            return new Summary(
                layout.Facilities.Count,
                layout.Facilities.Sum(facility => facility.Zones.Count),
                layout.Facilities.Sum(facility => facility.Spots.Count),
                counts.Sum(),
                counts[(int)ProjectionOutcome.Applied],
                counts[(int)ProjectionOutcome.Duplicate],
                counts[(int)ProjectionOutcome.Stale],
                counts[(int)ProjectionOutcome.UnknownSpot],
                elapsed);
        }
    }
}
