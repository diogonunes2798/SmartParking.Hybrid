using System.Net;
using System.Text.Json;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Facilities;

[Collection(ApiCollectionDefinition.Name)]
public sealed class DeleteFacilityTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task Delete_FacilityWithSpots_Returns409AndWritesNothing_AC5()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, etag) = await FacilityApi.CreateAsync(client, cancellationToken);

        // Zones and spots have no endpoints until F2b: insert them as the application login.
        var app = fixture.Databases.AppConnectionString;
        var zoneId = Guid.CreateVersion7();
        await DbProbe.ExecuteAsync(app, "INSERT INTO zones (id, facility_id, name) VALUES ($1, $2, 'Z1')",
            cancellationToken, zoneId, facility.Id);
        await DbProbe.ExecuteAsync(app,
            "INSERT INTO parking_spots (id, facility_id, zone_id, code, type, status) VALUES ($1, $2, $3, 'A-001', 'Standard', 'Active')",
            cancellationToken, Guid.CreateVersion7(), facility.Id, zoneId);

        // Act
        using var request = FacilityApi.WithIfMatch(HttpMethod.Delete, facility.Id, etag);
        using var response = await client.SendAsync(request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var stillThere = await client.GetAsync(new Uri($"{FacilityApi.Route}/{facility.Id}", UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        Assert.Equal(["Created|FacilityCreated.v1"], await FacilityApi.AuditTrailAsync(Owner, facility.Id, cancellationToken));
        Assert.Equal(["FacilityCreated.v1"], await FacilityApi.OutboxTypesAsync(Owner, facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Delete_EmptyFacilityWithHours_Returns204CascadesHoursAndAudits_AC6()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        OpeningHoursRequest[] week =
        [
            new(1, new TimeOnly(8, 0), new TimeOnly(20, 0)),
            new(6, new TimeOnly(9, 0), new TimeOnly(13, 0)),
        ];
        var (facility, etag) = await FacilityApi.CreateAsync(client, cancellationToken, FacilityApi.NewRequest(hours: week));

        // Act
        using var request = FacilityApi.WithIfMatch(HttpMethod.Delete, facility.Id, etag);
        using var response = await client.SendAsync(request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var hours = await DbProbe.ScalarAsync<long>(
            Owner, "SELECT count(*) FROM facility_opening_hours WHERE facility_id = $1", cancellationToken, facility.Id);
        Assert.Equal(0, hours);

        Assert.Equal(
            ["Created|FacilityCreated.v1", "Deleted|FacilityDeleted.v1"],
            await FacilityApi.AuditTrailAsync(Owner, facility.Id, cancellationToken));

        var deleted = await DbProbe.ScalarAsync<string>(
            Owner,
            "SELECT json_build_object('before', before, 'after', after)::text FROM audit_entries WHERE entity_id = $1 AND operation = 'Deleted'",
            cancellationToken,
            facility.Id);
        using var row = JsonDocument.Parse(deleted);
        Assert.Equal(JsonValueKind.Null, row.RootElement.GetProperty("after").ValueKind);
        Assert.Equal(2, row.RootElement.GetProperty("before").GetProperty("openingHours").GetArrayLength());
    }
}
