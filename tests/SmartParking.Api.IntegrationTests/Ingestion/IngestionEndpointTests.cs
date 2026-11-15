using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using SmartParking.Application.Ingestion;

namespace SmartParking.Api.IntegrationTests.Ingestion;

/// <summary>
/// UC-01 through HTTP, up to MongoDB: POST → Redis stream → consumer (as in Workers) → occupancy_events.
/// Each test uses fresh eventIds, so tests do not depend on what earlier tests left in the shared stream.
/// </summary>
[Collection(IngestionTestGroup.Name)]
public sealed class IngestionEndpointTests(IngestionFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_eventId_posted_twice_is_stored_once()
    {
        await using var _ = await fixture.StartOccupancyLogConsumerAsync();
        var eventId = Guid.NewGuid();
        object[] batch = [IngestionFixture.Event(eventId)];

        var first = await PostAsync(batch);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await IngestionFixture.EventuallyAsync(async () => await fixture.CountAsync(eventId) == 1, "first copy stored");

        // The API does not filter across requests (by design): the repeat is published again and the unique index
        // on eventId turns it into a no-op. Wait until the second copy has been consumed before counting.
        var marker = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(batch)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync([IngestionFixture.Event(marker)])).StatusCode);
        await IngestionFixture.EventuallyAsync(async () => await fixture.CountAsync(marker) == 1, "later event stored");

        Assert.Equal(1, await fixture.CountAsync(eventId));
    }

    [Fact]
    public async Task Duplicates_within_one_batch_are_published_once()
    {
        var eventId = Guid.NewGuid();

        var response = await PostAsync([IngestionFixture.Event(eventId), IngestionFixture.Event(eventId)]);

        var body = await ReadAcceptedAsync(response);
        Assert.Equal(1, body.Accepted);
        Assert.Equal(1, body.DuplicatesInBatch);
        Assert.Empty(body.Rejected);
    }

    /// <summary>
    /// The specification does not say whether one invalid event rejects only itself or the whole batch.
    /// TODO(Diogo): confirm with the supervisor; the default tested here is partial acceptance
    /// (IngestionSettings.RejectWholeBatchOnInvalid = false).
    /// </summary>
    [Fact]
    public async Task Batch_with_one_invalid_event_accepts_the_valid_ones_and_reports_the_invalid_one()
    {
        await using var _ = await fixture.StartOccupancyLogConsumerAsync();
        var valid1 = Guid.NewGuid();
        var valid2 = Guid.NewGuid();
        var invalidId = Guid.NewGuid();
        var invalid = IngestionFixture.Event(invalidId);
        invalid["type"] = "PARKED";
        invalid["timestamp"] = "2026-11-14T10:00:00"; // no offset

        var response = await PostAsync([IngestionFixture.Event(valid1), invalid, IngestionFixture.Event(valid2)]);

        var body = await ReadAcceptedAsync(response);
        Assert.Equal(2, body.Accepted);
        var rejected = Assert.Single(body.Rejected);
        Assert.Equal(1, rejected.Index);
        Assert.Equal(invalidId, rejected.EventId);
        Assert.Equal(2, rejected.Errors.Count);

        await IngestionFixture.EventuallyAsync(
            async () => await fixture.CountAsync(valid1) == 1 && await fixture.CountAsync(valid2) == 1, "valid events stored");
        Assert.Equal(0, await fixture.CountAsync(invalidId));
    }

    [Fact]
    public async Task Batch_without_any_valid_event_is_400_with_the_reasons()
    {
        var invalid = IngestionFixture.Event();
        invalid.Remove("sensorId");

        var response = await PostAsync([invalid]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var rejected = problem.RootElement.GetProperty("rejected");
        Assert.Equal(1, rejected.GetArrayLength());
        Assert.Contains("'sensorId' is required.", rejected[0].GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"eventId\":\"x\"}")]
    [InlineData("[{")]
    public async Task Empty_non_array_or_unreadable_body_is_400(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await fixture.Client.PostAsync("/api/ingest/occupancy", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Trap named in the phase brief: a string timestamp would break the (facilityId, timestamp) index.</summary>
    [Fact]
    public async Task Stored_document_has_date_timestamp_uuid_ids_and_the_payload_as_received()
    {
        await using var _ = await fixture.StartOccupancyLogConsumerAsync();
        var eventId = Guid.NewGuid();
        // UC-05: a newer sensor generation sends extra fields, nested values and arrays.
        var payload = new Dictionary<string, object?>
        {
            ["temperature"] = 21.5,
            ["airQuality"] = new Dictionary<string, object?> { ["pm25"] = 12, ["index"] = "good" },
            ["noise"] = new[] { 41.2, 43.0 },
            ["firmware"] = "2.1.0",
        };

        await PostAsync([IngestionFixture.Event(eventId, payload, "2026-11-14T11:00:00.123+01:00")]);
        await IngestionFixture.EventuallyAsync(async () => await fixture.CountAsync(eventId) == 1, "event stored");

        var doc = await fixture.FindAsync(eventId);
        Assert.Equal(BsonType.DateTime, doc["timestamp"].BsonType);
        Assert.Equal(new DateTime(2026, 11, 14, 10, 0, 0, 123, DateTimeKind.Utc), doc["timestamp"].ToUniversalTime());
        Assert.Equal(BsonBinarySubType.UuidStandard, doc["eventId"].AsBsonBinaryData.SubType);
        Assert.Equal("OCCUPIED", doc["type"].AsString);
        Assert.Equal(BsonType.DateTime, doc["receivedAt"].BsonType);
        Assert.Equal(BsonType.DateTime, doc["storedAt"].BsonType);

        var stored = doc["payload"].AsBsonDocument;
        Assert.Equal(21.5, stored["temperature"].AsDouble);
        Assert.Equal(12, stored["airQuality"]["pm25"].AsInt32);
        Assert.Equal("good", stored["airQuality"]["index"].AsString);
        // The BSON number type follows the JSON text as received: System.Text.Json writes 43.0 as "43", which
        // becomes an Int32. Readers of payload fields (Phase 6/7) must not assume one numeric type (see DIARIO).
        var noise = stored["noise"].AsBsonArray;
        Assert.Equal([BsonType.Double, BsonType.Int32], noise.Select(v => v.BsonType));
        Assert.Equal([41.2, 43.0], noise.Select(v => v.ToDouble()));
        Assert.Equal("2.1.0", stored["firmware"].AsString);
    }

    private Task<HttpResponseMessage> PostAsync(object[] batch) =>
        fixture.Client.PostAsJsonAsync("/api/ingest/occupancy", batch, IngestionFixture.Json, Ct);

    private static async Task<IngestAccepted> ReadAcceptedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IngestAccepted>(IngestionFixture.Json, Ct))!;
    }
}
