using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartParking.Api.Tests.Fixtures;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Ingestion;

/// <summary>The batch schema: any failure is a 400 for the whole batch and nothing is stored (spec 003, RF-01, AC-2, AC-3).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class IngestionSchemaTests(ApiFixture fixture)
{
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

    public static TheoryData<string> MalformedCases() =>
    [
        "empty batch",
        "501 events",
        "eventId not a UUID",
        "type not Occupied or Freed",
        "timestamp without zone",
        "payload not an object",
        "payload above 4 KB",
    ];

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task Ingest_MalformedBatch_Returns400AndStoresNothing_AC3(string scenario)
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
                broken["payload"] = new JsonObject { ["blob"] = new string('x', 4096) };
                break;
        }

        // Act
        using var response = await PostAsync(client, Batch(events), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var sentIds = events.Select(item => item["eventId"]!.GetValue<string>())
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(id => id != Guid.Empty);
        Assert.Empty(await FindAsync(Mongo, sentIds, cancellationToken));
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
}
