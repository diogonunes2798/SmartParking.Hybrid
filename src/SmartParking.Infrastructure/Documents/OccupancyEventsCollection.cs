using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// The <c>occupancy_events</c> collection as the ingestion writes it (spec 003, RNF-03): write concern
/// <c>majority</c> with journal, so the gateway is confirmed only after the event is in the journal of most
/// replica set members (AC-14). A normal collection, not time series: those do not allow unique indexes.
/// </summary>
internal sealed class OccupancyEventsCollection(IMongoClient client, IOptions<MongoOptions> options)
{
    public const string Name = "occupancy_events";

    public IMongoCollection<OccupancyEventDocument> Collection { get; } = client
        .GetDatabase(options.Value.Database)
        .GetCollection<OccupancyEventDocument>(Name)
        .WithWriteConcern(WriteConcern.WMajority.With(journal: true));
}
