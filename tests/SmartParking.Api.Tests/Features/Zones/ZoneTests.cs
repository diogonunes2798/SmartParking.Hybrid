using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Zones;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Zones;

/// <summary>Zones of a facility (spec 001, F2b): AC-13 and the zone-specific cases without an AC.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class ZoneTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task Delete_ZoneWithSpots_Returns409AndWritesNothing_AC13()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var (zone, etag) = await ConfigurationApi.CreateZoneAsync(client, facility.Id, cancellationToken);
        await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.Zones(facility.Id)}/{zone.Id}", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["Created|ZoneCreated.v1"], await FacilityApi.AuditTrailAsync(Owner, zone.Id, cancellationToken));
        Assert.DoesNotContain("ZoneDeleted.v1", await ConfigurationApi.FacilityOutboxTypesAsync(Owner, facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Delete_EmptyZone_Returns204WithAuditAndEvent()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var (zone, etag) = await ConfigurationApi.CreateZoneAsync(client, facility.Id, cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.Zones(facility.Id)}/{zone.Id}", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            ["Created|ZoneCreated.v1", "Deleted|ZoneDeleted.v1"],
            await FacilityApi.AuditTrailAsync(Owner, zone.Id, cancellationToken));
    }

    [Fact]
    public async Task Get_ZoneOfOtherFacility_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (_, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);
        var (other, _) = await FacilityApi.CreateAsync(client, cancellationToken);

        // Act
        using var response = await client.GetAsync(
            new Uri($"{ConfigurationApi.Zones(other.Id)}/{zone.Id}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownFacility_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Zones(Guid.CreateVersion7()), new ZoneRequest("Piso 0"), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_RepeatedNameInFacility_Returns409()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, zone) = await ConfigurationApi.CreateFacilityWithZoneAsync(client, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Zones(facility.Id), new ZoneRequest(zone.Name), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Put_NewName_Returns200WithNewETagAndZoneUpdated()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var (zone, etag) = await ConfigurationApi.CreateZoneAsync(client, facility.Id, cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Put, $"{ConfigurationApi.Zones(facility.Id)}/{zone.Id}", etag, cancellationToken,
            new ZoneRequest($"Renamed {Guid.NewGuid():N}"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        Assert.Equal(
            ["Created|ZoneCreated.v1", "Updated|ZoneUpdated.v1"],
            await FacilityApi.AuditTrailAsync(Owner, zone.Id, cancellationToken));
    }
}
