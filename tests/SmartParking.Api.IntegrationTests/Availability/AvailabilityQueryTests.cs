using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.IntegrationTests.Relational;
using SmartParking.Application.Availability;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Cache.Availability;

namespace SmartParking.Api.IntegrationTests.Availability;

/// <summary>
/// UC-03 — <c>GET /api/availability</c>: Redis GEO and hashes in the hybrid variant, one PostGIS query in the monolithic
/// one (Phase 8, same contract, run in both modes). The coordinates are real places in
/// Coimbra (approximate), chosen so that swapping latitude and longitude changes the answer: «Portagem» is about
/// 1.72 km from the centre of the query, but 2.19 km if the two coordinates were swapped (east–west distances
/// shrink with cos(latitude) at 40° N, not at 8° S). A radius of 2 km must include it.
/// </summary>
[Collection(AvailabilityTestGroup.Name)]
public sealed class AvailabilityQueryTests(AvailabilityFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Query centre (the example of the Phase 4 instructions).
    private const double Lat = 40.2033;
    private const double Lon = -8.4103;

    private static readonly (string Name, double Lat, double Lon, double Metres) Portagem = ("Largo da Portagem", 40.2089, -8.4292, 1722);
    private static readonly (string Name, double Lat, double Lon, double Metres) Isec = ("ISEC", 40.1925, -8.4121, 1211);
    private static readonly (string Name, double Lat, double Lon, double Metres) CoimbraB = ("Estação Coimbra-B", 40.2228, -8.4357, 3059);

    [Fact]
    public async Task Radius_search_returns_only_the_facilities_inside_the_radius_nearest_first()
    {
        var portagem = await fixture.SeedAsync(2, Portagem.Lat, Portagem.Lon);
        var isec = await fixture.SeedAsync(3, Isec.Lat, Isec.Lon);
        var coimbraB = await fixture.SeedAsync(1, CoimbraB.Lat, CoimbraB.Lon);
        HashSet<Guid> mine = [portagem.Facility, isec.Facility, coimbraB.Facility];

        var all = await GetAsync($"/api/availability?lat={Lat}&lon={Lon}&radius=2000");
        var found = all.Where(f => mine.Contains(f.FacilityId)).ToList();

        Assert.Equal([isec.Facility, portagem.Facility], found.Select(f => f.FacilityId)); // nearest first
        Assert.DoesNotContain(found, f => f.FacilityId == coimbraB.Facility);

        // Distances computed by Redis (or PostGIS on a sphere, about 0.03 % apart) agree with the haversine distance of the real points (±1 %): a swap of
        // latitude and longitude would give 2191 m for Portagem.
        Assert.InRange(found[1].DistanceMeters, Portagem.Metres * 0.99, Portagem.Metres * 1.01);
        Assert.InRange(found[0].DistanceMeters, Isec.Metres * 0.99, Isec.Metres * 1.01);
        Assert.All(found, f => Assert.True(f.DistanceMeters <= 2000));

        // Location in the answer is GeoJSON, longitude first; counts come from the facility hash.
        var p = found[1];
        Assert.Equal([Portagem.Lon, Portagem.Lat], p.Location.Coordinates);
        Assert.Equal(2, p.Spots);
        Assert.Equal(2, p.Free);

        // A larger radius reaches Coimbra-B.
        var wide = await GetAsync($"/api/availability?lat={Lat}&lon={Lon}&radius=3500");
        Assert.Contains(wide, f => f.FacilityId == coimbraB.Facility);
    }

    [HybridFact]
    public async Task Geo_set_stores_longitude_then_latitude()
    {
        var seeded = await fixture.SeedAsync(1, Portagem.Lat, Portagem.Lon);
        var position = await fixture.Db.GeoPositionAsync(ReadModelKeys.Geo, seeded.Facility.ToString());

        Assert.NotNull(position);
        Assert.Equal(Portagem.Lon, position.Value.Longitude, 4);
        Assert.Equal(Portagem.Lat, position.Value.Latitude, 4);
    }

    [Fact]
    public async Task Type_filter_counts_only_spots_of_that_type_and_skips_facilities_without_them()
    {
        var cars = await fixture.SeedAsync(2, Isec.Lat, Isec.Lon, VehicleType.Car);
        var electric = await fixture.SeedAsync(3, Isec.Lat, Isec.Lon, VehicleType.Electric);
        await fixture.SetStateAsync(
            electric.Facility, electric.Spots[0], SpotState.Occupied, DateTimeOffset.FromUnixTimeMilliseconds(1_763_200_000_000));

        var found = await GetAsync($"/api/availability?lat={Lat}&lon={Lon}&radius=2000&type=Electric");

        Assert.DoesNotContain(found, f => f.FacilityId == cars.Facility);
        var e = Assert.Single(found, f => f.FacilityId == electric.Facility);
        Assert.Equal(3, e.Spots);
        Assert.Equal(2, e.Free);
    }

    [Theory]
    [InlineData("?lon=-8.41&radius=1000", "lat")]
    [InlineData("?lat=40.2&radius=1000", "lon")]
    [InlineData("?lat=91&lon=-8.41", "lat")]
    [InlineData("?lat=40.2&lon=-181", "lon")]
    [InlineData("?lat=40.2&lon=-8.41&radius=0", "radius")]
    [InlineData("?lat=40.2&lon=-8.41&radius=50001", "radius")]
    [InlineData("?lat=40.2&lon=-8.41&limit=0", "limit")]
    public async Task Invalid_parameters_are_rejected(string query, string field)
    {
        var response = await fixture.Client.GetAsync("/api/availability" + query, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_vehicle_type_is_rejected()
    {
        var response = await fixture.Client.GetAsync("/api/availability?lat=40.2&lon=-8.41&type=Bus", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<List<Result>> GetAsync(string url) =>
        (await fixture.Client.GetFromJsonAsync<List<Result>>(
            url.Replace(",", ".", StringComparison.Ordinal), RelationalFixture.Json, Ct))!;

    private sealed record Location(string Type, List<double> Coordinates);

    private sealed record Result(
        Guid FacilityId, string Name, string Zone, FacilityType Type, Location Location, int Capacity,
        double DistanceMeters, int Spots, int Free);
}
