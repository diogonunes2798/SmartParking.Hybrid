using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Features.Ingestion;
using SmartParking.Api.Features.Sensors;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Features.Zones;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Ingestion requests for the tests (spec 003). It registers facilities, zones, spots and sensors through the F2
/// endpoints. Events are built as JSON so that a test can leave a field out. The event log is read straight from
/// MongoDB, always filtered by the test's own <c>eventId</c>s, because the database is shared.
/// </summary>
internal static class IngestionApi
{
    public const string Route = "/api/ingest/occupancy-events";

    public const string Database = "smartparking";

    public const string Collection = "occupancy_events";

    public sealed record SpotWithSensor(Guid SpotId, Guid SensorId, string HardwareId);

    public sealed record Site(CreateFacilityRequest Request, FacilityResponse Facility, ZoneResponse Zone, IReadOnlyList<SpotWithSensor> Spots);

    /// <summary>A facility with one zone and <paramref name="count"/> active spots, each with an active occupancy sensor.</summary>
    public static async Task<Site> CreateSiteAsync(HttpClient client, int count, CancellationToken cancellationToken)
    {
        var request = FacilityApi.NewRequest();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken, request);
        var (zone, _) = await ConfigurationApi.CreateZoneAsync(client, facility.Id, cancellationToken);
        var hardwareIds = Enumerable.Range(0, count).Select(_ => ConfigurationApi.NewHardwareId()).ToList();
        SpotBatchItemRequest[] spots =
        [
            .. hardwareIds.Select(hardwareId => new SpotBatchItemRequest(
                zone.Id,
                ConfigurationApi.NewCode(),
                "Standard",
                new SpotBatchSensorRequest(hardwareId, "OccuSense 2", new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero)))),
        ];

        using var response = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<SpotBatchResponse>(cancellationToken);

        return new Site(
            request,
            facility,
            zone,
            [.. created!.Items.Select((item, index) => new SpotWithSensor(item.SpotId, item.SensorId!.Value, hardwareIds[index]))]);
    }

    /// <summary>A valid event of this sensor; <paramref name="timestamp"/> defaults to one minute before <paramref name="now"/>.</summary>
    public static JsonObject Event(
        SpotWithSensor spot, DateTimeOffset now, string type = "Occupied", DateTimeOffset? timestamp = null, Guid? eventId = null) =>
        Event(spot.HardwareId, spot.SpotId, now, type, timestamp, eventId);

    public static JsonObject Event(
        string hardwareId, Guid spotId, DateTimeOffset now, string type = "Occupied", DateTimeOffset? timestamp = null, Guid? eventId = null) => new()
        {
            ["eventId"] = (eventId ?? Guid.NewGuid()).ToString(),
            ["sensorId"] = hardwareId,
            ["spotId"] = spotId.ToString(),
            ["type"] = type,
            ["timestamp"] = (timestamp ?? now.AddMinutes(-1)).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };

    public static JsonObject Batch(params JsonObject[] events) => new() { ["events"] = new JsonArray([.. events]) };

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonNode batch, CancellationToken cancellationToken) =>
        PostAsync(client, batch.ToJsonString(), cancellationToken);

    /// <summary>Sends the body as written, for JSON that <see cref="JsonNode"/> cannot build (a repeated member, a huge number).</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(Route, UriKind.Relative), content, cancellationToken);
    }

    public static async Task<OccupancyBatchResponse> PostOkAsync(HttpClient client, JsonNode batch, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(client, batch, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OccupancyBatchResponse>(cancellationToken))!;
    }

    public static Guid IdOf(JsonObject occupancyEvent) => Guid.Parse(occupancyEvent["eventId"]!.GetValue<string>());

    public static IMongoCollection<BsonDocument> Events(string connectionString) =>
        new MongoClient(connectionString).GetDatabase(Database).GetCollection<BsonDocument>(Collection);

    /// <summary>The stored documents with these ids.</summary>
    public static Task<List<BsonDocument>> FindAsync(string connectionString, IEnumerable<Guid> eventIds, CancellationToken cancellationToken) =>
        Events(connectionString)
            .Find(Builders<BsonDocument>.Filter.In("_id", eventIds.Select(id => new BsonBinaryData(id, GuidRepresentation.Standard))))
            .ToListAsync(cancellationToken);

    public static BsonBinaryData Uuid(Guid id) => new(id, GuidRepresentation.Standard);

    /// <summary>Changes the configuration through the F2 endpoints (GET for the ETag, then PUT with <c>If-Match</c>).</summary>
    public static async Task UpdateSensorStatusAsync(HttpClient client, Guid sensorId, string status, CancellationToken cancellationToken)
    {
        var route = $"{ConfigurationApi.SensorsRoute}/{sensorId}";
        var (sensor, etag) = await ConfigurationApi.GetAsync<SensorResponse>(client, route, cancellationToken);
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, route, etag, cancellationToken, new UpdateSensorRequest(sensor.Model, status, null));
        response.EnsureSuccessStatusCode();
    }

    public static async Task UpdateSpotAsync(
        HttpClient client, Guid facilityId, Guid spotId, CancellationToken cancellationToken, Guid? zoneId = null, string? status = null)
    {
        var route = $"{ConfigurationApi.Spots(facilityId)}/{spotId}";
        var (spot, etag) = await ConfigurationApi.GetAsync<SpotResponse>(client, route, cancellationToken);
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, route, etag, cancellationToken, new UpdateSpotRequest(zoneId ?? spot.ZoneId, spot.Code, spot.Type, status ?? spot.Status));
        response.EnsureSuccessStatusCode();
    }

    public static async Task DeactivateFacilityAsync(HttpClient client, Site site, CancellationToken cancellationToken)
    {
        var route = $"{FacilityApi.Route}/{site.Facility.Id}";
        var (facility, etag) = await ConfigurationApi.GetAsync<FacilityResponse>(client, route, cancellationToken);
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, route, etag, cancellationToken, FacilityApi.UpdateOf(site.Request, facility.Name, "Inactive"));
        response.EnsureSuccessStatusCode();
    }
}
