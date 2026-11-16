using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Infrastructure.Cache.Availability;
using SmartParking.Infrastructure.Documents.OccupancyEvents;
using SmartParking.SensorSimulator;
using DocumentsRegistration = SmartParking.Infrastructure.Documents.DependencyInjection;

namespace SmartParking.Api.IntegrationTests.Availability;

/// <summary>
/// UC-05 / RNF-04 (Phase 7): a second generation of sensors adds <c>temperature</c>, <c>airQuality</c> and
/// <c>noise</c> to <c>payload</c>. With the SensorSimulator itself (provisioning through RF-06, real-time sending in
/// batches), the test checks that no part of the pipeline rejects or loses the new fields: the API accepts every
/// event (202, nothing rejected), the log writer stores the three fields with their values and types in MongoDB, and
/// the availability projector applies generation-2 events like any other (the last event of each spot is in Redis).
/// </summary>
[Collection(AvailabilityTestGroup.Name)]
public sealed class SchemaEvolutionTests(AvailabilityFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generation_two_events_reach_MongoDB_with_the_new_fields_and_update_the_read_model()
    {
        // Mid-run step from generation 1 to "all generation 2", so that both generations travel in the same run.
        var options = new ScenarioOptions
        {
            Name = $"uc05-{Guid.NewGuid():N}"[..20],
            Facilities = 1,
            Spots = 4,
            Rate = 20,
            DurationSeconds = 3,
            Gen2InitialRatio = 0,
            Gen2Ratio = 1,
            Gen2RampStartSeconds = 1.5,
            Seed = 2026,
            BatchSize = 10,
            BatchWindowMs = 50,
            WaitReadModelSeconds = 15,
        };

        await using var workers = await fixture.StartWorkersAsync();

        // 1. Topology through the RF-06 API, then the configuration events to the read model (outbox → bus).
        var provisioner = new Provisioner(fixture.Client, options, TextWriter.Null);
        var topology = await provisioner.ProvisionAsync(Ct);
        foreach (var id in topology.FacilityIds.Concat(topology.SpotIds).Concat(topology.SensorIds))
        {
            await fixture.RelayAsync(await fixture.OutboxAsync(id));
        }

        Assert.True(await provisioner.WaitForReadModelAsync(topology, Ct), "read model never learned the simulated spots");

        // 2. Send the run.
        var expected = new OccupancyEventGenerator(options).Generate().ToList();
        var summary = await new LoadRunner(fixture.Client, options, topology, TextWriter.Null)
            .RunAsync(new OccupancyEventGenerator(options).Generate(), Ct);

        Assert.True(summary.Clean, summary.ToString());
        Assert.Equal(expected.Count, summary.Sent);
        Assert.Equal(summary.Sent, summary.Accepted);
        Assert.Contains(expected, e => e.Generation == 1);
        Assert.Contains(expected, e => e.Generation == 2);
        Assert.Equal(expected.Count(e => e.Generation == 2), summary.SentGen2);

        // 3. MongoDB: every event stored once, with the payload as sent.
        var events = fixture.Services.GetRequiredService<IMongoClient>()
            .GetDatabase(DocumentsRegistration.DefaultDatabaseName)
            .GetCollection<BsonDocument>(MongoOccupancyEventLog.CollectionName);
        var ids = expected.Select(e => e.EventId).ToList();
        var filter = Builders<BsonDocument>.Filter.In("eventId", ids.Select(id => new BsonBinaryData(id, GuidRepresentation.Standard)));
        await Ingestion.IngestionFixture.EventuallyAsync(
            async () => await events.CountDocumentsAsync(filter, cancellationToken: Ct) == expected.Count,
            "every simulated event in the MongoDB log");

        var stored = (await events.Find(filter).ToListAsync(Ct))
            .ToDictionary(d => d["eventId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard));
        foreach (var e in expected)
        {
            var payload = stored[e.EventId]["payload"].AsBsonDocument;
            Assert.Equal(e.Generation, payload[EventJsonWriter.GenerationField].ToInt32());
            if (e.Generation == 2)
            {
                Assert.Equal(BsonType.Double, payload[EventJsonWriter.TemperatureField].BsonType);
                Assert.Equal(e.Readings.Temperature, payload[EventJsonWriter.TemperatureField].AsDouble);
                Assert.Equal(e.Readings.AirQuality, payload[EventJsonWriter.AirQualityField].AsInt32);
                Assert.Equal(BsonType.Double, payload[EventJsonWriter.NoiseField].BsonType);
                Assert.Equal(e.Readings.Noise, payload[EventJsonWriter.NoiseField].AsDouble);
            }
            else
            {
                Assert.Equal(1, payload.ElementCount); // only sensorGeneration
            }
        }

        // 4. Redis: the last event of each spot (generation 1 or 2) is the one the read model applied; all spots end FREE.
        foreach (var last in expected.GroupBy(e => e.SpotIndex).Select(g => g.Last()))
        {
            var key = ReadModelKeys.Spot(topology.SpotIds[last.SpotIndex]);
            await Ingestion.IngestionFixture.EventuallyAsync(
                async () => (string?)await fixture.Db.HashGetAsync(key, "eventId") == last.EventId.ToString(),
                $"last event of spot {last.SpotIndex} applied to the read model");
            Assert.Equal("FREE", (string?)await fixture.Db.HashGetAsync(key, "state"));
        }

        Assert.Equal(options.Spots, (await fixture.FacilityCountersAsync(topology.FacilityIds[0]))["free"]);
    }
}
