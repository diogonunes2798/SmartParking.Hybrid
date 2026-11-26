using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartParking.Api.Features.Ingestion;
using SmartParking.Api.Tests.Fixtures;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Ingestion;

/// <summary>The batch schema: any failure is a 400 for the whole batch and nothing is stored (spec 003, RF-01, AC-2, AC-3).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class IngestionSchemaTests(ApiFixture fixture)
{
    private const int MaxPayloadBytes = OccupancyBatchRequestValidator.MaxPayloadBytes;

    private static readonly DateTimeOffset Now = new(2026, 12, 3, 8, 30, 0, TimeSpan.Zero);

    private string Mongo => fixture.Databases.Mongo.GetConnectionString();

    [Theory]
    [InlineData("eventId")]
    [InlineData("sensorId")]
    [InlineData("spotId")]
    [InlineData("type")]
    [InlineData("timestamp")]
    public async Task Ingest_SecondEventMissingField_Returns400WithIndexedKeyAndStoresNothing_AC2(string field)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 2, cancellationToken);
        var first = Event(site.Spots[0], Now);
        var second = Event(site.Spots[1], Now);
        var secondId = IdOf(second);
        second.Remove(field);

        // Act
        using var response = await PostAsync(client, Batch(first, second), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty($"events[1].{field}", out _));
        Assert.Empty(await FindAsync(Mongo, [IdOf(first), secondId], cancellationToken));
    }

    /// <summary>The raw JSON that replaces the <c>payload</c> marker; some shapes (a repeated member) cannot be built with JsonObject.</summary>
    private const string PayloadMarker = "\"__payload__\"";

    /// <summary>Each malformed batch and the <c>errors</c> key that must name it.</summary>
    public static TheoryData<string, string> MalformedCases() => new()
    {
        { "empty batch", "events" },
        { "501 events", "events" },
        { "eventId not a UUID", "events[1].eventId" },
        { "type not Occupied or Freed", "events[1].type" },
        { "timestamp without zone", "events[1].timestamp" },
        { "payload not an object", "events[1].payload" },
        { "payload above 4 KB", "events[1].payload" },
        { "payload repeats a member", "events[1].payload" },
        { "payload with extended JSON", "events[1].payload" },
        { "payload with a non-finite number", "events[1].payload" },
        { "payload with a $ member name", "events[1].payload" },
        { "payload with a lone surrogate", "events[1].payload" },
    };

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task Ingest_MalformedBatch_Returns400NamingTheFieldAndStoresNothing_AC3(string scenario, string errorKey)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        var spot = site.Spots[0];
        var valid = Event(spot, Now);
        var broken = Event(spot, Now, timestamp: Now.AddSeconds(-30));
        JsonObject[] events = scenario switch
        {
            "empty batch" => [],
            "501 events" => [.. Enumerable.Range(0, 501).Select(_ => Event(spot, Now))],
            _ => [valid, broken],
        };
        switch (scenario)
        {
            case "eventId not a UUID":
                broken["eventId"] = "evt-42";
                break;
            case "type not Occupied or Freed":
                broken["type"] = "Reserved";
                break;
            case "timestamp without zone":
                broken["timestamp"] = "2026-12-03T08:29:00";
                break;
            case "payload not an object":
                broken["payload"] = new JsonArray(1, 2);
                break;
            case "payload above 4 KB":
                broken["payload"] = PayloadOfBytes(MaxPayloadBytes + 1);
                break;
            case "payload repeats a member":
                broken["payload"] = "__payload__";
                break;
            case "payload with extended JSON":
                broken["payload"] = JsonNode.Parse("""{ "t": { "$date": "2026-01-01T00:00:00Z" } }""");
                break;
            case "payload with a non-finite number":
                broken["payload"] = "__payload__";
                break;
            case "payload with a $ member name":
                broken["payload"] = new JsonObject { ["$set"] = 1 };
                break;
            case "payload with a lone surrogate":
                broken["payload"] = "__payload__";
                break;
        }

        var raw = scenario switch
        {
            "payload repeats a member" => """{ "a": 1, "a": 2 }""",
            "payload with a non-finite number" => """{ "d": 1e400 }""",
            "payload with a lone surrogate" => """{ "s": "\uD800" }""",
            _ => null,
        };
        var body = Batch(events).ToJsonString();
        if (raw is not null)
        {
            body = body.Replace(PayloadMarker, raw, StringComparison.Ordinal);
        }

        // Act
        using var response = await PostAsync(client, body, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(errorKey, out _), $"errors has no '{errorKey}': {errors}");
        var sentIds = events.Select(item => item["eventId"]!.GetValue<string>())
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(id => id != Guid.Empty);
        Assert.Empty(await FindAsync(Mongo, sentIds, cancellationToken));
    }

    [Fact]
    public async Task Ingest_PayloadOfExactly4096Bytes_IsAccepted_AC3()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        var occupancyEvent = Event(site.Spots[0], Now);
        occupancyEvent["payload"] = PayloadOfBytes(MaxPayloadBytes);

        // Act
        var outcome = await PostOkAsync(client, Batch(occupancyEvent), cancellationToken);

        // Assert
        Assert.Equal(1, outcome.Accepted);
        Assert.Single(await FindAsync(Mongo, [IdOf(occupancyEvent)], cancellationToken));
    }

    [Fact]
    public async Task Ingest_500ValidEvents_IsAccepted_AC3()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        JsonObject[] events = [.. Enumerable.Range(0, 500).Select(i => Event(site.Spots[0], Now, timestamp: Now.AddSeconds(-i - 1)))];

        // Act
        var outcome = await PostOkAsync(client, Batch(events), cancellationToken);

        // Assert
        Assert.Equal(500, outcome.Accepted);
        Assert.Equal(500, (await FindAsync(Mongo, events.Select(IdOf), cancellationToken)).Count);
    }

    /// <summary>A payload whose compact JSON has exactly <paramref name="bytes"/> bytes: <c>{"blob":"x…x"}</c>.</summary>
    private static JsonObject PayloadOfBytes(int bytes)
    {
        const int Overhead = 11; // {"blob":""}
        var payload = new JsonObject { ["blob"] = new string('x', bytes - Overhead) };
        Assert.Equal(bytes, System.Text.Encoding.UTF8.GetByteCount(payload.ToJsonString()));
        return payload;
    }
}
