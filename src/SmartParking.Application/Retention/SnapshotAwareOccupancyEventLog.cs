using System.Runtime.CompilerServices;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;

namespace SmartParking.Application.Retention;

/// <summary>
/// Decorator of the event log for the readers of the «last event of each spot» (UC-04 aggregation, ADR-0010; read model
/// rebuild, ADR-0008) once retention has removed old events (Phase 10, ADR-0013). For each spot it returns the newer of
/// the log's answer and the snapshot entry (when that entry is before the requested instant). A spot whose last event
/// was removed therefore keeps its state; a spot with a newer event in the log is unaffected.
/// <para>Appends and range reads go to the log unchanged.</para>
/// </summary>
public sealed class SnapshotAwareOccupancyEventLog(IOccupancyEventLog inner, ISpotStateSnapshotStore snapshots) : IOccupancyEventLog
{
    public Task<AppendResult> AppendAsync(IReadOnlyList<OccupancyEventRecord> records, CancellationToken cancellationToken) =>
        inner.AppendAsync(records, cancellationToken);

    public async IAsyncEnumerable<LatestSpotEvent> GetLatestPerSpotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fromSnapshot = (await snapshots.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.Event.SpotId, s => s.Event);
        await foreach (var latest in inner.GetLatestPerSpotAsync(cancellationToken).ConfigureAwait(false))
        {
            if (fromSnapshot.Remove(latest.SpotId, out var snapshot) && IsNewer(snapshot, latest))
            {
                yield return snapshot;
            }
            else
            {
                yield return latest;
            }
        }

        foreach (var remaining in fromSnapshot.Values)
        {
            yield return remaining; // spots with no event left in the log
        }
    }

    public async Task<IReadOnlyList<LatestSpotEvent>> GetLatestPerSpotAsync(
        Guid facilityId, DateTimeOffset before, CancellationToken cancellationToken)
    {
        var fromLog = await inner.GetLatestPerSpotAsync(facilityId, before, cancellationToken).ConfigureAwait(false);
        var fromSnapshot = await snapshots.GetAsync(facilityId, cancellationToken).ConfigureAwait(false);
        if (fromSnapshot.Count == 0)
        {
            return fromLog;
        }

        var merged = fromLog.ToDictionary(e => e.SpotId);
        foreach (var entry in fromSnapshot)
        {
            var snapshot = entry.Event;
            if (snapshot.Timestamp >= before)
            {
                continue; // after the requested instant: not the state before it
            }

            if (!merged.TryGetValue(snapshot.SpotId, out var current) || IsNewer(snapshot, current))
            {
                merged[snapshot.SpotId] = snapshot;
            }
        }

        return [.. merged.Values];
    }

    public Task<IReadOnlyList<SpotOccupancyEvent>> GetEventsAsync(
        Guid facilityId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
        inner.GetEventsAsync(facilityId, since, until, cancellationToken);

    /// <summary>The order of the read model and of the aggregation: timestamp, then event id (ADR-0008).</summary>
    private static bool IsNewer(LatestSpotEvent candidate, LatestSpotEvent current) =>
        candidate.Timestamp > current.Timestamp
        || (candidate.Timestamp == current.Timestamp && candidate.EventId.CompareTo(current.EventId) > 0);
}
