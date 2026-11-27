using MongoDB.Driver;
using SmartParking.Application.Abstractions.Documents;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// Indexes of <c>occupancy_events</c> beyond the mandatory <c>_id</c> (spec 004, «Índice no MongoDB»):
/// <c>{ spotId: 1, timestamp: -1, _id: -1 }</c> serves the last event per spot of the rebuild. Creating an index that
/// already exists with the same keys and name is a no-op in MongoDB, so this is idempotent (AC-12). The write cost of
/// the index is attributed to F5 (spec 003, ronda 6).
/// </summary>
internal sealed class OccupancyEventIndexes(OccupancyEventsCollection events) : IOccupancyEventIndexes
{
    public const string LatestPerSpotIndexName = "spotId_1_timestamp_-1__id_-1";

    public Task EnsureAsync(CancellationToken cancellationToken)
    {
        var keys = Builders<OccupancyEventDocument>.IndexKeys
            .Ascending(document => document.SpotId)
            .Descending(document => document.Timestamp)
            .Descending(document => document.EventId);

        return events.Collection.Indexes.CreateOneAsync(
            new CreateIndexModel<OccupancyEventDocument>(keys, new CreateIndexOptions { Name = LatestPerSpotIndexName }),
            cancellationToken: cancellationToken);
    }
}
