using System.Net;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.ProjectionRebuilder;
using static SmartParking.Api.Tests.Fixtures.AvailabilityApi;
using static SmartParking.Api.Tests.Fixtures.AvailabilityView;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Availability;

/// <summary>
/// <c>GET /api/availability/nearby</c> (spec 004, AC-16 to AC-18; RF-04). The facilities are created through the Api
/// north of a fixed point, at known distances, and the view is built by the real rebuild in an isolated environment,
/// whose Redis database holds only this test's facilities.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class NearbySearchTests(ApiFixture fixture)
{
    private const double Latitude = 40.0;
    private const double Longitude = -8.0;

    /// <summary>Metres per degree of latitude on the Redis sphere.</summary>
    private const double MetersPerDegree = Math.PI * RedisEarthRadiusMeters / 180;

    [Fact]
    public async Task SearchNearby_SortByDistance_ReturnsActiveInRadiusAscending_AC16()
    {
        // Arrange: active facilities at 3 km, 1 km, 2 km and 6 km (out of the radius); an inactive one at 1.5 km.
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        using var client = factory.CreateClient();
        var at3 = await CreateNorthAsync(client, 3_000, 1, cancellationToken);
        var at1 = await CreateNorthAsync(client, 1_000, 1, cancellationToken);
        var at2 = await CreateNorthAsync(client, 2_000, 1, cancellationToken);
        await CreateNorthAsync(client, 6_000, 1, cancellationToken);
        var inactive = await CreateNorthAsync(client, 1_500, 1, cancellationToken);
        await DeactivateFacilityAsync(client, inactive, cancellationToken);
        Assert.Equal(ExitCodes.Success, await RunRebuilderAsync(environment, cancellationToken));

        // Act
        var answer = await NearbyOkAsync(client, $"lat={Text(Latitude)}&lon={Text(Longitude)}&radiusMeters=5000", cancellationToken);

        // Assert
        Assert.Equal([at1.Facility.Id, at2.Facility.Id, at3.Facility.Id], answer.Facilities.Select(facility => facility.FacilityId));
        foreach (var (facility, site) in answer.Facilities.Zip([at1, at2, at3]))
        {
            var expected = HaversineMeters(Latitude, Longitude, site.Request.Latitude, site.Request.Longitude);
            Assert.InRange(facility.DistanceMeters, expected - 1, expected + 1);
            Assert.Equal((site.Facility.Name, site.Facility.OperatorId, 1, 1), (facility.Name, facility.OperatorId, facility.Free, facility.Total));
        }
    }

    [Fact]
    public async Task SearchNearby_SortByFreeWithLimit_TieBrokenByDistance_AC17()
    {
        // Arrange: free spots 1 km → 1, 2 km → 3, 3 km → 3 (2 km wins the tie), 4 km → 3 with one occupied → 2.
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        using var client = factory.CreateClient();
        await CreateNorthAsync(client, 1_000, 1, cancellationToken);
        var at3 = await CreateNorthAsync(client, 3_000, 3, cancellationToken);
        var at2 = await CreateNorthAsync(client, 2_000, 3, cancellationToken);
        var at4 = await CreateNorthAsync(client, 4_000, 3, cancellationToken);
        Assert.Equal(ExitCodes.Success, await RunRebuilderAsync(environment, cancellationToken));
        await OccupyAsync(factory.Services, at4, at4.Spots[0], cancellationToken);

        // Act
        var answer = await NearbyOkAsync(client, $"lat={Text(Latitude)}&lon={Text(Longitude)}&radiusMeters=10000&sort=free&limit=2", cancellationToken);

        // Assert
        Assert.Equal(
            [(at2.Facility.Id, 3, 3), (at3.Facility.Id, 3, 3)],
            answer.Facilities.Select(facility => (facility.FacilityId, facility.Free, facility.Total)));
    }

    [Theory]
    [InlineData("lon=-8&radiusMeters=1000")]
    [InlineData("lat=85.06&lon=-8&radiusMeters=1000")]
    [InlineData("lat=-90&lon=-8&radiusMeters=1000")]
    [InlineData("lat=40&radiusMeters=1000")]
    [InlineData("lat=40&lon=181&radiusMeters=1000")]
    [InlineData("lat=40&lon=-180.5&radiusMeters=1000")]
    [InlineData("lat=40&lon=-8")]
    [InlineData("lat=40&lon=-8&radiusMeters=0")]
    [InlineData("lat=40&lon=-8&radiusMeters=50001")]
    [InlineData("lat=40&lon=-8&radiusMeters=1000&sort=name")]
    [InlineData("lat=40&lon=-8&radiusMeters=1000&sort=Free")]
    [InlineData("lat=40&lon=-8&radiusMeters=1000&limit=0")]
    [InlineData("lat=40&lon=-8&radiusMeters=1000&limit=101")]
    [InlineData("lat=40&lon=-8&radiusMeters=1000&spotType=Car")]
    public async Task SearchNearby_OutOfRange_Returns400_AC18(string query)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await NearbyAsync(client, query, cancellationToken);

        // Assert
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, cancellationToken);
        Assert.True(problem.TryGetProperty("errors", out _), "A 400 must be ValidationProblemDetails.");
    }

    [Theory]
    [InlineData("lat=85.05112878&lon=180&radiusMeters=1&sort=distance&limit=1")]
    [InlineData("lat=-85.05112878&lon=-180&radiusMeters=50000&sort=free&limit=100&spotType=Accessible")]
    public async Task SearchNearby_AtTheLimits_Returns200_AC18(string query)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await NearbyAsync(client, query, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A facility with <paramref name="spots"/> Standard spots, <paramref name="meters"/> north of the point.</summary>
    private static Task<Site> CreateNorthAsync(HttpClient client, double meters, int spots, CancellationToken cancellationToken) =>
        CreateSiteAsync(client, spots, cancellationToken,
            FacilityApi.NewRequest() with { Latitude = Latitude + (meters / MetersPerDegree), Longitude = Longitude });
}
