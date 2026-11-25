using System.Text.Json.Nodes;
using MongoDB.Bson;
using SmartParking.Api.Tests.Fixtures;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Ingestion;

/// <summary>Accepted batches, idempotency, the extensible payload and the spot context (spec 003, AC-1, AC-4, AC-5, AC-9, AC-10).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class IngestionBatchTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 12, 3, 8, 30, 0, TimeSpan.Zero);

    private string Mongo => fixture.Databases.Mongo.GetConnectionString();

    [Fact]
    public async Task Ingest_OneEventPerSensor_Returns200AndStoresEachEvent_AC1()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 3, cancellationToken);
        JsonObject[] events = [.. site.Spots.Select(spot => Event(spot, Now))];

        // Act
        var outcome = await PostOkAsync(client, Batch(events), cancellationToken);

        // Assert
        Assert.Equal(3, outcome.Accepted);
        Assert.Empty(outcome.Issues);
        var stored = (await FindAsync(Mongo, events.Select(IdOf), cancellationToken)).ToDictionary(document => document["_id"].AsGuid);
        Assert.Equal(3, stored.Count);
        foreach (var (spot, sent) in site.Spots.Zip(events))
        {
            var document = stored[IdOf(sent)];
            Assert.Equal(spot.HardwareId, document["hardwareId"].AsString);
            Assert.Equal(spot.SensorId, document["sensorId"].AsGuid);
            Assert.Equal(spot.SpotId, document["spotId"].AsGuid);
            Assert.Equal(site.Facility.Id, document["facilityId"].AsGuid);
            Assert.Equal(site.Zone.Id, document["zoneId"].AsGuid);
            Assert.Equal("Occupied", document["type"].AsString);
            Assert.Equal(Now.AddMinutes(-1).UtcDateTime, document["timestamp"].ToUniversalTime());
            Assert.Equal(Now.UtcDateTime, document["receivedAt"].ToUniversalTime());
        }
    }

    [Fact]
    public async Task Ingest_SameBatchTwice_SecondIsAllDuplicatesAndDocumentsUnchanged_AC4()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 3, cancellationToken);
        var batch = Batch([.. site.Spots.Select(spot => Event(spot, Now))]);
        var ids = batch["events"]!.AsArray().Select(node => IdOf(node!.AsObject())).ToList();
        await PostOkAsync(client, batch, cancellationToken);
        var before = await FindAsync(Mongo, ids, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        var outcome = await PostOkAsync(client, batch, cancellationToken);

        // Assert
        Assert.Equal(0, outcome.Accepted);
        Assert.Equal(3, outcome.Duplicates);
        Assert.Equal(0, outcome.Rejected);
        Assert.Equal([0, 1, 2], outcome.Issues.Select(issue => issue.Index));
        Assert.All(outcome.Issues, issue => Assert.Equal(("duplicate", (string?)null), (issue.Status, issue.Code)));
        var after = await FindAsync(Mongo, ids, cancellationToken);
        Assert.Equal(
            before.OrderBy(document => document["_id"].AsGuid).Select(document => document.ToJson()),
            after.OrderBy(document => document["_id"].AsGuid).Select(document => document.ToJson()));
    }

    [Fact]
    public async Task Ingest_RepeatedEventIdInBatch_FirstWinsSecondIsDuplicate_AC5()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 2, cancellationToken);
        var eventId = Guid.NewGuid();
        var first = Event(site.Spots[0], Now, "Occupied", eventId: eventId);
        var second = Event(site.Spots[1], Now, "Freed", eventId: eventId);

        // Act
        var outcome = await PostOkAsync(client, Batch(first, second), cancellationToken);

        // Assert
        Assert.Equal(1, outcome.Accepted);
        var issue = Assert.Single(outcome.Issues);
        Assert.Equal((1, eventId, "duplicate"), (issue.Index, issue.EventId, issue.Status));
        var document = Assert.Single(await FindAsync(Mongo, [eventId], cancellationToken));
        Assert.Equal(site.Spots[0].SpotId, document["spotId"].AsGuid);
        Assert.Equal("Occupied", document["type"].AsString);
    }

    [Fact]
    public async Task Ingest_PayloadWithNestedAndUnknownTopField_StoresPayloadAndDropsTopField_AC9()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 2, cancellationToken);
        var withPayload = Event(site.Spots[0], Now);
        withPayload["payload"] = JsonNode.Parse("""{ "battery": 87, "firmware": { "v": "2.1" } }""");
        withPayload["temperature"] = 21.5;
        var withoutPayload = Event(site.Spots[1], Now);

        // Act
        var outcome = await PostOkAsync(client, Batch(withPayload, withoutPayload), cancellationToken);

        // Assert
        Assert.Equal(2, outcome.Accepted);
        var stored = (await FindAsync(Mongo, [IdOf(withPayload), IdOf(withoutPayload)], cancellationToken))
            .ToDictionary(document => document["_id"].AsGuid);
        var document = stored[IdOf(withPayload)];
        Assert.Equal(BsonDocument.Parse("""{ "battery": 87, "firmware": { "v": "2.1" } }"""), document["payload"].AsBsonDocument);
        Assert.False(document.Contains("temperature"));
        Assert.False(stored[IdOf(withoutPayload)].Contains("payload"));
    }

    [Fact]
    public async Task Ingest_SpotMovedToAnotherZone_OldEventKeepsOldZone_AC10()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        var spot = site.Spots[0];
        var (zone2, _) = await ConfigurationApi.CreateZoneAsync(client, site.Facility.Id, cancellationToken);
        var first = Event(spot, Now, "Occupied");
        await PostOkAsync(client, Batch(first), cancellationToken);
        await UpdateSpotAsync(client, site.Facility.Id, spot.SpotId, cancellationToken, zoneId: zone2.Id);
        var second = Event(spot, Now, "Freed", timestamp: Now.AddSeconds(-10));

        // Act
        var outcome = await PostOkAsync(client, Batch(second), cancellationToken);

        // Assert
        Assert.Equal(1, outcome.Accepted);
        var stored = (await FindAsync(Mongo, [IdOf(first), IdOf(second)], cancellationToken)).ToDictionary(document => document["_id"].AsGuid);
        Assert.Equal(site.Zone.Id, stored[IdOf(first)]["zoneId"].AsGuid);
        Assert.Equal(zone2.Id, stored[IdOf(second)]["zoneId"].AsGuid);
    }
}
