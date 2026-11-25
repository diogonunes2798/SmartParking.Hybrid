using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Spots;

/// <summary>Spot batches of 1 to 1 000, all or nothing (spec 001, AC-15).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class SpotBatchTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task Batch_1000SpotsWithSensors_Returns201AndCreatesAllWithAuditAndOutbox_AC15()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var installedAt = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        SpotBatchItemRequest[] spots =
        [
            .. Enumerable.Range(1, 1000).Select(i => new SpotBatchItemRequest(
                zone.Id, $"B-{i:D4}", "Standard", new SpotBatchSensorRequest(ConfigurationApi.NewHardwareId(), "OccuSense 2", installedAt))),
        ];

        // Act
        using var response = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SpotBatchResponse>(cancellationToken);
        Assert.Equal(1000, body!.Items.Count);
        Assert.All(body.Items, item => Assert.NotNull(item.SensorId));

        Assert.Equal(1000, await CountAsync("SELECT count(*) FROM parking_spots WHERE facility_id = $1", facility.Id, cancellationToken));
        Assert.Equal(
            1000,
            await CountAsync("SELECT count(*) FROM sensors WHERE facility_id = $1 AND spot_id IS NOT NULL AND kind = 'Occupancy'", facility.Id, cancellationToken));
        Assert.Equal(
            2000,
            await CountAsync(
                """
                SELECT count(*) FROM audit_entries a
                WHERE a.operation = 'Created'
                  AND (a.entity_id IN (SELECT id FROM parking_spots WHERE facility_id = $1)
                       OR a.entity_id IN (SELECT id FROM sensors WHERE facility_id = $1))
                """,
                facility.Id,
                cancellationToken));
        Assert.Equal(
            2000,
            await CountAsync(
                "SELECT count(*) FROM outbox_messages WHERE payload->>'facilityId' = $1::text AND type IN ('SpotCreated.v1', 'SensorRegistered.v1')",
                facility.Id,
                cancellationToken));
    }

    [Fact]
    public async Task Batch_1001Spots_Returns400_AC15()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        SpotBatchItemRequest[] spots = [.. Enumerable.Range(1, 1001).Select(i => new SpotBatchItemRequest(zone.Id, $"C-{i:D4}", "Standard", null))];

        // Act
        using var response = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("spots", out _));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM parking_spots WHERE facility_id = $1", facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Batch_ExistingCode_Returns409AndCreatesNothing_AC15()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken, "D-0003");
        SpotBatchItemRequest[] spots =
        [
            .. Enumerable.Range(1, 5).Select(i => new SpotBatchItemRequest(
                zone.Id, $"D-{i:D4}", "Standard", new SpotBatchSensorRequest(ConfigurationApi.NewHardwareId(), "OccuSense 2", DateTimeOffset.UnixEpoch))),
        ];

        // Act
        using var response = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM parking_spots WHERE facility_id = $1", facility.Id, cancellationToken));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM sensors WHERE facility_id = $1", facility.Id, cancellationToken));
        Assert.Equal(["ZoneCreated.v1", "SpotCreated.v1"], await ConfigurationApi.FacilityOutboxTypesAsync(Owner, facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Batch_RepeatedHardwareIdInRequest_Returns400()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var hardwareId = ConfigurationApi.NewHardwareId();
        SpotBatchItemRequest[] spots =
        [
            new(zone.Id, "E-0001", "Standard", new SpotBatchSensorRequest(hardwareId, "OccuSense 2", DateTimeOffset.UnixEpoch)),
            new(zone.Id, "E-0002", "Standard", new SpotBatchSensorRequest(hardwareId, "OccuSense 2", DateTimeOffset.UnixEpoch)),
        ];

        // Act
        using var response = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM parking_spots WHERE facility_id = $1", facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Batch_RepeatedCodeInRequest_Returns400()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        SpotBatchItemRequest[] spots = [new(zone.Id, "F-0001", "Standard", null), new(zone.Id, "F-0001", "Accessible", null)];

        // Act
        using var response = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_ZoneOfOtherFacility_Returns400WithItemKey()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (_, otherZone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        SpotBatchItemRequest[] spots = [new(zone.Id, "G-0001", "Standard", null), new(otherZone.Id, "G-0002", "Standard", null)];

        // Act
        using var response = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("spots[1].zoneId", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"spots":null}""")]
    [InlineData("""{"spots":[null]}""")]
    public async Task Batch_MissingOrNullSpots_Returns400(string body)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        // Act
        using var response = await client.PostAsync(new Uri($"{ConfigurationApi.Spots(facility.Id)}/batch", UriKind.Relative), content, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("spots", out _));
    }

    private Task<long> CountAsync(string sql, Guid facilityId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<long>(Owner, sql, cancellationToken, facilityId);
}
