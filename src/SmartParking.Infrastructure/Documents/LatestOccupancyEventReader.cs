using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// The last event of each spot (spec 004, rebuild step 3): sort by <c>spotId</c> ascending and by <c>timestamp</c> and
/// <c>_id</c> descending (the order of <see cref="OccupancyEventIndexes"/>), then keep the first document of each spot.
/// <c>_id</c> is a standard UUID (binary subtype 4, big-endian), whose byte order is the order of its lowercase text, so
/// the tie-break matches the projector's (ADR 0010). Read-only: <c>aggregate</c> with no write stage.
/// </summary>
internal sealed class LatestOccupancyEventReader(OccupancyEventsCollection events) : ILatestOccupancyEventReader
{
    private static readonly PipelineDefinition<OccupancyEventDocument, OccupancyEventDocument> LatestPerSpot =
        PipelineDefinition<OccupancyEventDocument, OccupancyEventDocument>.Create(
        [
            new BsonDocument("$sort", new BsonDocument { { "spotId", 1 }, { "timestamp", -1 }, { "_id", -1 } }),
            new BsonDocument("$group", new BsonDocument { { "_id", "$spotId" }, { "latest", new BsonDocument("$first", "$$ROOT") } }),
            new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$latest")),
        ]);

    private static readonly AggregateOptions Options = new() { AllowDiskUse = true };

    public async IAsyncEnumerable<OccupancyEvent> ReadLatestPerSpotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cursor = await events.Collection.AggregateAsync(LatestPerSpot, Options, cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                yield return new OccupancyEvent(
                    document.EventId,
                    document.SensorId,
                    document.HardwareId,
                    document.SpotId,
                    document.FacilityId,
                    document.ZoneId,
                    document.Type,
                    new DateTimeOffset(document.Timestamp, TimeSpan.Zero),
                    new DateTimeOffset(document.ReceivedAt, TimeSpan.Zero),
                    Payload: null);
            }
        }
    }
}
