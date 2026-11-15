using System.Diagnostics;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Ingestion;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Availability;

/// <summary>Counts of a rebuild (logged, and checked by the tests and the benchmarks).</summary>
public sealed record RebuildReport(int Facilities, int Spots, int Events, int EventsApplied, TimeSpan Duration);

/// <summary>
/// Rebuilds the availability read model from scratch (ADR-0008): clears it, loads facilities and spots from the
/// configuration store (relational, RF-06), then applies the last event of every spot from the occupancy log
/// (MongoDB, D3). Used for the initial load of the Workers and by the <c>rebuild-read-model</c> command
/// (benchmarks start from a known state, Phase 9).
/// <para>
/// Safe with live projectors and with other rebuilds (review of Phase 4, findings 1 and 2):
/// <list type="number">
/// <item>the rebuild takes a lock (<see cref="IAvailabilityReadModel.BeginRebuildAsync"/>); a second rebuild fails
/// with <see cref="RebuildInProgressException"/>, and every projector write is refused (and retried by the bus) until
/// the lock is released, so nothing can recreate a key while it is being cleared;</item>
/// <item>before reading the MongoDB log, it records the position up to which the log writer group
/// (<see cref="OccupancyLogConsumer.Name"/>) has acknowledged everything; at the end, it moves the availability
/// group back to that position. Events acknowledged by the availability group but not yet in the log are then
/// delivered again; being idempotent and ordered by timestamp, every event only moves a spot forward.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ReadModelRebuilder(
    IAvailabilityReadModel readModel,
    IAvailabilitySource source,
    IOccupancyEventLog log,
    IConsumerGroupPositions positions,
    ReadModelMetrics metrics,
    TimeProvider time)
{
    /// <summary>Writes sent to Redis before waiting for their replies (pipelining, bounded memory).</summary>
    private const int ChunkSize = 500;

    /// <summary>
    /// The configuration snapshot is stamped this much earlier than the start of the rebuild. A change whose event
    /// time is before the start but whose transaction commits after the snapshot was read would otherwise look
    /// stale when its event arrives and be lost; re-applying a full-state event already in the snapshot is harmless.
    /// </summary>
    public static readonly TimeSpan SnapshotMargin = TimeSpan.FromSeconds(30);

    public async Task<RebuildReport> RebuildAsync(CancellationToken cancellationToken)
    {
        var session = await readModel.BeginRebuildAsync(cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var started = time.GetUtcNow();
            var stopwatch = Stopwatch.StartNew();
            var changedAt = started - SnapshotMargin;

            // Everything up to here is in the MongoDB log (the writer acknowledges only after writing).
            var logged = await positions.GetAcknowledgedPositionAsync(
                OccupancyEventMessage.Topic, OccupancyLogConsumer.Name, cancellationToken).ConfigureAwait(false);

            await session.ClearAsync(cancellationToken).ConfigureAwait(false);

            // Facilities first: a spot's counters go to its facility's zone.
            var facilities = await InChunksAsync(
                source.GetFacilitiesAsync(cancellationToken),
                f => session.UpsertFacilityAsync(f, changedAt, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var spots = await InChunksAsync(
                source.GetSpotsAsync(cancellationToken),
                s => session.UpsertSpotAsync(s, changedAt, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var events = 0;
            var applied = 0;
            var chunk = new List<OccupancyChange>(ChunkSize);
            await foreach (var e in log.GetLatestPerSpotAsync(cancellationToken).ConfigureAwait(false))
            {
                chunk.Add(new OccupancyChange(
                    e.EventId,
                    e.SpotId,
                    e.Type == OccupancyEventType.Occupied ? SpotState.Occupied : SpotState.Free,
                    e.Timestamp.ToUnixTimeMilliseconds()));
                if (chunk.Count == ChunkSize)
                {
                    applied += await ApplyAsync(session, chunk, cancellationToken).ConfigureAwait(false);
                    events += chunk.Count;
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
            {
                applied += await ApplyAsync(session, chunk, cancellationToken).ConfigureAwait(false);
                events += chunk.Count;
            }

            // Redeliver to the projector whatever the log did not have yet (applied after the lock is released).
            await positions.SetPositionAsync(
                OccupancyEventMessage.Topic, AvailabilityConsumers.Occupancy, logged, cancellationToken).ConfigureAwait(false);
            await session.MarkLoadedAsync(started, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            metrics.Rebuilt(stopwatch.Elapsed);
            return new RebuildReport(facilities, spots, events, applied, stopwatch.Elapsed);
        }
    }

    private static async Task<int> ApplyAsync(IReadModelRebuildSession session, List<OccupancyChange> changes, CancellationToken cancellationToken)
    {
        var outcomes = await session.ApplyAsync(changes, cancellationToken).ConfigureAwait(false);
        return outcomes.Count(o => o is ApplyOutcome.Applied or ApplyOutcome.Unchanged);
    }

    private static async Task<int> InChunksAsync<T>(
        IAsyncEnumerable<T> items, Func<T, Task<ConfigurationOutcome>> write, CancellationToken cancellationToken)
    {
        var count = 0;
        var pending = new List<Task<ConfigurationOutcome>>(ChunkSize);
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            pending.Add(write(item));
            count++;
            if (pending.Count == ChunkSize)
            {
                await Task.WhenAll(pending).ConfigureAwait(false); // observes every task; rethrows the first failure
                pending.Clear();
            }
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        return count;
    }
}
