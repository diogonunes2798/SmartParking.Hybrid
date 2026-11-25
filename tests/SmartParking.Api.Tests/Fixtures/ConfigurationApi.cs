using System.Net.Http.Json;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Features.Sensors;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Features.Tariffs;
using SmartParking.Api.Features.Zones;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Requests for zones, spots, sensors and tariff versions (spec 001, F2b): unique data per test, ETag handling and
/// the queries that count audit entries and outbox messages per facility.
/// </summary>
internal static class ConfigurationApi
{
    public const string SensorsRoute = "/api/sensors";

    public static string Zones(Guid facilityId) => $"{FacilityApi.Route}/{facilityId}/zones";

    public static string Spots(Guid facilityId) => $"{FacilityApi.Route}/{facilityId}/spots";

    public static string Tariffs(Guid facilityId) => $"{FacilityApi.Route}/{facilityId}/tariffs";

    public static string NewCode() => $"S-{Guid.NewGuid():N}"[..20];

    public static string NewHardwareId() => $"HW-{Guid.NewGuid():N}";

    /// <summary>A facility with one zone, the usual starting point.</summary>
    public static async Task<(FacilityResponse Facility, ZoneResponse Zone)> CreateFacilityWithZoneAsync(
        HttpClient client, CancellationToken cancellationToken)
    {
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var (zone, _) = await CreateZoneAsync(client, facility.Id, cancellationToken);
        return (facility, zone);
    }

    public static Task<(ZoneResponse Zone, string ETag)> CreateZoneAsync(
        HttpClient client, Guid facilityId, CancellationToken cancellationToken, string? name = null) =>
        PostAsync<ZoneResponse>(client, Zones(facilityId), new ZoneRequest(name ?? $"Zone {Guid.NewGuid():N}"), cancellationToken);

    public static Task<(SpotResponse Spot, string ETag)> CreateSpotAsync(
        HttpClient client, Guid facilityId, Guid zoneId, CancellationToken cancellationToken, string? code = null) =>
        PostAsync<SpotResponse>(
            client, Spots(facilityId), new CreateSpotRequest(zoneId, code ?? NewCode(), "Standard"), cancellationToken);

    public static RegisterSensorRequest NewSensor(Guid facilityId, Guid? spotId = null, string kind = "Occupancy", string? hardwareId = null) =>
        new(facilityId, hardwareId ?? NewHardwareId(), kind, "OccuSense 2", new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), spotId, null);

    public static Task<(SensorResponse Sensor, string ETag)> RegisterSensorAsync(
        HttpClient client, RegisterSensorRequest request, CancellationToken cancellationToken) =>
        PostAsync<SensorResponse>(client, SensorsRoute, request, cancellationToken);

    public static CreateTariffRequest NewTariff(DateTimeOffset validFrom, decimal pricePerHour = 1.20m) =>
        new(validFrom, pricePerHour, 15, 12m, 10, null);

    public static async Task<(T Body, string ETag)> PostAsync<T>(
        HttpClient client, string route, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(route, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        return (created!, response.Headers.ETag!.Tag);
    }

    public static async Task<(T Body, string ETag)> GetAsync<T>(HttpClient client, string route, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(route, UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        return (body!, response.Headers.ETag!.Tag);
    }

    /// <summary>Sends a request with an optional <c>If-Match</c> and JSON body.</summary>
    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string route, string? etag, CancellationToken cancellationToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(route, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return SendAndDisposeAsync(client, request, cancellationToken);
    }

    /// <summary>Outbox types whose payload names this facility, oldest first.</summary>
    public static Task<List<string>> FacilityOutboxTypesAsync(string connectionString, Guid facilityId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            connectionString,
            "SELECT type FROM outbox_messages WHERE payload->>'facilityId' = $1 ORDER BY occurred_at, id",
            cancellationToken,
            facilityId.ToString());

    /// <summary>The payload of the only outbox message of this type and entity.</summary>
    public static Task<string> PayloadAsync(string connectionString, Guid entityId, string type, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<string>(
            connectionString,
            "SELECT payload::text FROM outbox_messages WHERE payload->>'id' = $1 AND type = $2",
            cancellationToken,
            entityId.ToString(),
            type);

    /// <summary>
    /// Inserts, as the owner, a version in force since yesterday. The API only accepts versions that start now or
    /// later, so a version in force cannot be created through it (plan 001b, point 8).
    /// </summary>
    public static async Task<Guid> SeedCurrentTariffAsync(string ownerConnectionString, Guid facilityId, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        await DbProbe.ExecuteAsync(
            ownerConnectionString,
            """
            INSERT INTO tariffs (id, facility_id, valid_from, valid_to, price_per_hour, billing_increment_minutes, daily_maximum, free_minutes, currency)
            VALUES ($1, $2, now() - interval '1 day', NULL, 1.00, 15, NULL, 0, 'EUR')
            """,
            cancellationToken,
            id,
            facilityId);
        return id;
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            return await client.SendAsync(request, cancellationToken);
        }
    }
}
