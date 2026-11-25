using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Features.Sensors;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sensors;

/// <summary>Sensor registry (spec 001, F2b): AC-11 (sensor) and AC-14, plus the no-op cases (plan 001b, point 6).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class SensorTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task Register_DuplicateHardwareId_Returns409_AC11()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var first = ConfigurationApi.NewSensor(facility.Id);
        await ConfigurationApi.RegisterSensorAsync(client, first, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.SensorsRoute, ConfigurationApi.NewSensor(facility.Id, hardwareId: first.HardwareId), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["SensorRegistered.v1"], await ConfigurationApi.FacilityOutboxTypesAsync(Owner, facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Register_EnvironmentalWithSpot_Returns400_AC14()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (spot, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.SensorsRoute, ConfigurationApi.NewSensor(facility.Id, spot.Id, kind: "Environmental"), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("spotId", out _));
    }

    [Fact]
    public async Task Reassign_ToFreeSpotSameFacility_Returns200WithSensorReassigned_AC14()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (l1, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (l2, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, l1.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}/spot", etag, cancellationToken, new AssignSensorRequest(l2.Id));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(l2.Id, (await response.Content.ReadFromJsonAsync<SensorResponse>(cancellationToken))!.SpotId);
        using var payload = JsonDocument.Parse(await ConfigurationApi.PayloadAsync(Owner, sensor.Id, "SensorReassigned.v1", cancellationToken));
        Assert.Equal(l1.Id, payload.RootElement.GetProperty("previousSpotId").GetGuid());
        Assert.Equal(l2.Id, payload.RootElement.GetProperty("spotId").GetGuid());
    }

    [Fact]
    public async Task Reassign_ToSpotWithSensor_Returns409_AC14()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (l1, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (l2, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, l1.Id), cancellationToken);
        await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, l2.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}/spot", etag, cancellationToken, new AssignSensorRequest(l2.Id));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["Created|SensorRegistered.v1"], await FacilityApi.AuditTrailAsync(Owner, sensor.Id, cancellationToken));
    }

    [Fact]
    public async Task Reassign_ToSpotOfOtherFacility_Returns400_AC14()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (l1, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (other, otherZone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (foreign, _) = await ConfigurationApi.CreateSpotAsync(client, other.Id, otherZone.Id, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, l1.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}/spot", etag, cancellationToken, new AssignSensorRequest(foreign.Id));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unassign_SensorOnSpot_ClearsSpotAndEmitsSensorUnassigned_AC14()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (l1, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, l1.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}/spot", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<SensorResponse>(cancellationToken))!.SpotId);
        var spotInDatabase = await DbProbe.ScalarAsync<bool>(
            Owner, "SELECT spot_id IS NULL FROM sensors WHERE id = $1", cancellationToken, sensor.Id);
        Assert.True(spotInDatabase);
        using var payload = JsonDocument.Parse(await ConfigurationApi.PayloadAsync(Owner, sensor.Id, "SensorUnassigned.v1", cancellationToken));
        Assert.Equal(l1.Id, payload.RootElement.GetProperty("previousSpotId").GetGuid());
    }

    [Fact]
    public async Task Reassign_ToSameSpot_Returns200WithoutAuditOrEvent()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (l1, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, l1.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}/spot", etag, cancellationToken, new AssignSensorRequest(l1.Id));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.Tag);
        Assert.Equal(["Created|SensorRegistered.v1"], await FacilityApi.AuditTrailAsync(Owner, sensor.Id, cancellationToken));
    }

    [Fact]
    public async Task Unassign_SensorWithoutSpot_Returns200WithoutAuditOrEvent()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}/spot", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.Tag);
        Assert.Equal(["Created|SensorRegistered.v1"], await FacilityApi.AuditTrailAsync(Owner, sensor.Id, cancellationToken));
    }

    [Fact]
    public async Task Remove_SensorStillOnSpot_Returns409()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (l1, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, l1.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Remove_SensorWithoutSpot_Returns204WithSensorRemoved()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var (sensor, etag) = await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.SensorsRoute}/{sensor.Id}", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            ["Created|SensorRegistered.v1", "Deleted|SensorRemoved.v1"],
            await FacilityApi.AuditTrailAsync(Owner, sensor.Id, cancellationToken));
    }
}
