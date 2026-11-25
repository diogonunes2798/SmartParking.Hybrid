using System.Net;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Facilities;

/// <summary>Optimistic concurrency with ETag/If-Match over <c>xmin</c> (spec 001, AC-12).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class UpdateFacilityTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task Put_StaleIfMatch_Returns412_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var created = FacilityApi.NewRequest();
        var (facility, staleETag) = await FacilityApi.CreateAsync(client, cancellationToken, created);
        using (var first = FacilityApi.WithIfMatch(HttpMethod.Put, facility.Id, staleETag, FacilityApi.UpdateOf(created, $"{created.Name} v2")))
        using (var firstResponse = await client.SendAsync(first, cancellationToken))
        {
            firstResponse.EnsureSuccessStatusCode();
        }

        // Act
        using var request = FacilityApi.WithIfMatch(HttpMethod.Put, facility.Id, staleETag, FacilityApi.UpdateOf(created, $"{created.Name} v3"));
        using var response = await client.SendAsync(request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var name = await DbProbe.ScalarAsync<string>(Owner, "SELECT name FROM parking_facilities WHERE id = $1", cancellationToken, facility.Id);
        Assert.Equal($"{created.Name} v2", name);
    }

    [Fact]
    public async Task Put_MissingIfMatch_Returns428_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var created = FacilityApi.NewRequest();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken, created);

        // Act
        using var request = FacilityApi.WithIfMatch(HttpMethod.Put, facility.Id, etag: null, FacilityApi.UpdateOf(created, $"{created.Name} v2"));
        using var response = await client.SendAsync(request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Put_CurrentETag_Returns200WithNewETag_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var created = FacilityApi.NewRequest();
        var (facility, etag) = await FacilityApi.CreateAsync(client, cancellationToken, created);

        // Act
        using var request = FacilityApi.WithIfMatch(
            HttpMethod.Put, facility.Id, etag, FacilityApi.UpdateOf(created, $"{created.Name} v2", status: "Inactive"));
        using var response = await client.SendAsync(request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag?.Tag);
        Assert.Equal(
            ["Created|FacilityCreated.v1", "Updated|FacilityUpdated.v1"],
            await FacilityApi.AuditTrailAsync(Owner, facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Put_OnlyOpeningHoursChanged_Returns200WithNewETagAndOldETagIsStale_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var created = FacilityApi.NewRequest();
        var (facility, etag) = await FacilityApi.CreateAsync(client, cancellationToken, created);
        var onlyHours = FacilityApi.UpdateOf(created, created.Name!) with
        {
            OpeningHours = [new OpeningHoursRequest(1, new TimeOnly(9, 0), new TimeOnly(18, 0))],
        };

        // Act
        using var request = FacilityApi.WithIfMatch(HttpMethod.Put, facility.Id, etag, onlyHours);
        using var response = await client.SendAsync(request, cancellationToken);
        using var stale = FacilityApi.WithIfMatch(HttpMethod.Put, facility.Id, etag, onlyHours with { OpeningHours = [] });
        using var staleResponse = await client.SendAsync(stale, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        var hours = await DbProbe.ScalarAsync<long>(
            Owner, "SELECT count(*) FROM facility_opening_hours WHERE facility_id = $1", cancellationToken, facility.Id);
        Assert.Equal(1, hours);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("W/\"1\"")]
    [InlineData("not-an-etag")]
    public async Task Put_IfMatchNotOneStrongETag_Returns412(string ifMatch)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var created = FacilityApi.NewRequest();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken, created);

        // Act
        using var request = FacilityApi.WithIfMatch(HttpMethod.Put, facility.Id, ifMatch, FacilityApi.UpdateOf(created, $"{created.Name} v2"));
        using var response = await client.SendAsync(request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Put_SameValues_Returns200WithSameETagAndNoAudit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var created = FacilityApi.NewRequest();
        var (facility, etag) = await FacilityApi.CreateAsync(client, cancellationToken, created);

        // Act
        using var request = FacilityApi.WithIfMatch(HttpMethod.Put, facility.Id, etag, FacilityApi.UpdateOf(created, created.Name!));
        using var response = await client.SendAsync(request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag?.Tag);
        Assert.Equal(["Created|FacilityCreated.v1"], await FacilityApi.AuditTrailAsync(Owner, facility.Id, cancellationToken));
    }
}
