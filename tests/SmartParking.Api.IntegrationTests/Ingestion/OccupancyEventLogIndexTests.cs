using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Application.Abstractions;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Documents.OccupancyEvents;

namespace SmartParking.Api.IntegrationTests.Ingestion;

/// <summary>Indexes of the occupancy log (spec §5: unique eventId; partitioning key (facilityId, timestamp)).</summary>
[Collection(IngestionTestGroup.Name)]
public sealed class OccupancyEventLogIndexTests(IngestionFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HybridFact]
    public async Task Log_has_unique_eventId_and_compound_facilityId_timestamp_indexes()
    {
        await fixture.Factory.Services.GetRequiredService<MongoOccupancyEventLog>().EnsureIndexesAsync(Ct);

        var indexes = await (await fixture.Events.Indexes.ListAsync(Ct)).ToListAsync(Ct);
        var unique = indexes.Single(i => i["name"] == MongoOccupancyEventLog.EventIdIndexName);
        var compound = indexes.Single(i => i["name"] == MongoOccupancyEventLog.FacilityTimestampIndexName);

        Assert.Equal(new BsonDocument("eventId", 1), unique["key"].AsBsonDocument);
        Assert.True(unique["unique"].ToBoolean());
        Assert.Equal(new BsonDocument { { "facilityId", 1 }, { "timestamp", 1 } }, compound["key"].AsBsonDocument);
    }

    [HybridFact]
    public async Task Range_query_by_facility_and_time_uses_the_compound_index_and_orders_by_date()
    {
        var log = fixture.Factory.Services.GetRequiredService<IOccupancyEventLog>();
        var facilityId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
        // Inserted out of order (9h, 10h, 8h); the query must return them in time order from the index alone.
        OccupancyEventRecord[] records =
        [
            Record(facilityId, start.AddHours(1)),
            Record(facilityId, start.AddHours(2)),
            Record(facilityId, start),
        ];
        await log.AppendAsync(records, Ct);

        var facility = new BsonBinaryData(facilityId, GuidRepresentation.Standard);
        var filter = new BsonDocument
        {
            { "facilityId", facility },
            { "timestamp", new BsonDocument { { "$gte", start.UtcDateTime.AddMinutes(30) }, { "$lt", start.UtcDateTime.AddHours(3) } } },
        };

        var found = await fixture.Events.Find(filter).Sort(new BsonDocument("timestamp", 1)).ToListAsync(Ct);
        Assert.Equal([start.AddHours(1).UtcDateTime, start.AddHours(2).UtcDateTime], found.Select(d => d["timestamp"].ToUniversalTime()));

        var explain = await fixture.Events.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument("explain", new BsonDocument
            {
                { "find", MongoOccupancyEventLog.CollectionName },
                { "filter", filter },
                { "sort", new BsonDocument("timestamp", 1) },
            }),
            cancellationToken: Ct);
        var plan = explain["queryPlanner"]["winningPlan"].ToJson();
        Assert.Contains("IXSCAN", plan, StringComparison.Ordinal);
        Assert.Contains(MongoOccupancyEventLog.FacilityTimestampIndexName, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SORT\"", plan, StringComparison.Ordinal); // the index already gives the order
    }

    private static OccupancyEventRecord Record(Guid facilityId, DateTimeOffset timestamp) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), facilityId, OccupancyEventType.Occupied, timestamp, "{}", timestamp);
}
