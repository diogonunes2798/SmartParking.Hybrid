using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Common;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Spots;

/// <summary>Parking spots one by one (spec 001, F2b): AC-11, AC-13, AC-16 and AC-18 (spots).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class SpotTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task Create_DuplicateCodeSameFacility_Returns409_AC11()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken, "A-001");

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Spots(facility.Id), new CreateSpotRequest(zone.Id, "A-001", "Standard"), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["ZoneCreated.v1", "SpotCreated.v1"], await ConfigurationApi.FacilityOutboxTypesAsync(Owner, facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Create_SameCodeOtherFacility_Returns201_AC11()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken, "A-001");
        var (other, otherZone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Spots(other.Id), new CreateSpotRequest(otherZone.Id, "A-001", "Standard"), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Create_ZoneOfOtherFacility_Returns400()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (_, otherZone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Spots(facility.Id), new CreateSpotRequest(otherZone.Id, "A-001", "Standard"), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_SpotWithSensor_Returns409AndWritesNothing_AC13()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (spot, etag) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        await ConfigurationApi.RegisterSensorAsync(client, ConfigurationApi.NewSensor(facility.Id, spot.Id), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.Spots(facility.Id)}/{spot.Id}", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["Created|SpotCreated.v1"], await FacilityApi.AuditTrailAsync(Owner, spot.Id, cancellationToken));
    }

    [Fact]
    public async Task Put_ZoneOfSameFacility_Returns200WithPreviousZoneId_AC16()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, z1) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (z2, _) = await ConfigurationApi.CreateZoneAsync(client, facility.Id, cancellationToken);
        var (spot, etag) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, z1.Id, cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.Spots(facility.Id)}/{spot.Id}", etag, cancellationToken,
            new UpdateSpotRequest(z2.Id, spot.Code, spot.Type, spot.Status));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SpotResponse>(cancellationToken);
        Assert.Equal(z2.Id, body!.ZoneId);

        using var payload = JsonDocument.Parse(await ConfigurationApi.PayloadAsync(Owner, spot.Id, "SpotUpdated.v1", cancellationToken));
        Assert.Equal(z1.Id, payload.RootElement.GetProperty("previousZoneId").GetGuid());
        Assert.Equal(z2.Id, payload.RootElement.GetProperty("zoneId").GetGuid());
    }

    [Fact]
    public async Task Put_ZoneOfOtherFacility_Returns400_AC16()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (_, otherZone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (spot, etag) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.Spots(facility.Id)}/{spot.Id}", etag, cancellationToken,
            new UpdateSpotRequest(otherZone.Id, spot.Code, spot.Type, spot.Status));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["Created|SpotCreated.v1"], await FacilityApi.AuditTrailAsync(Owner, spot.Id, cancellationToken));
    }

    [Fact]
    public async Task Put_MissingIfMatch_Returns428()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (spot, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.Spots(facility.Id)}/{spot.Id}", etag: null, cancellationToken,
            new UpdateSpotRequest(zone.Id, spot.Code, spot.Type, "Inactive"));

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task List_Page2Size10Of25_Returns10AndTotal25_AC18()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        SpotBatchItemRequest[] spots = [.. Enumerable.Range(1, 25).Select(i => new SpotBatchItemRequest(zone.Id, $"P-{i:D3}", "Standard", null))];
        using var batch = await client.PostAsJsonAsync($"{ConfigurationApi.Spots(facility.Id)}/batch", new SpotBatchRequest(spots), cancellationToken);
        batch.EnsureSuccessStatusCode();

        // Act
        var page = await client.GetFromJsonAsync<PagedResponse<SpotResponse>>(
            $"{ConfigurationApi.Spots(facility.Id)}?page=2&pageSize=10", cancellationToken);

        // Assert
        Assert.Equal(10, page!.Items.Count);
        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(25, page.TotalCount);
        Assert.All(page.Items, spot => Assert.Equal(facility.Id, spot.FacilityId));
    }

    [Fact]
    public async Task List_PageSize101_Returns400_AC18()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);

        // Act
        using var response = await client.GetAsync(
            new Uri($"{ConfigurationApi.Spots(facility.Id)}?pageSize=101", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_UnknownFacility_Returns404_AC18()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await client.GetAsync(
            new Uri(ConfigurationApi.Spots(Guid.CreateVersion7()), UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_FilterByZoneAndStatus_ReturnsOnlyMatchingSpots()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, z1) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (z2, _) = await ConfigurationApi.CreateZoneAsync(client, facility.Id, cancellationToken);
        await ConfigurationApi.CreateSpotAsync(client, facility.Id, z1.Id, cancellationToken);
        var (inZ2, _) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, z2.Id, cancellationToken);

        // Act
        var page = await client.GetFromJsonAsync<PagedResponse<SpotResponse>>(
            $"{ConfigurationApi.Spots(facility.Id)}?zoneId={z2.Id}&status=Active", cancellationToken);

        // Assert
        Assert.Equal([inZ2.Id], page!.Items.Select(spot => spot.Id));
    }
}
