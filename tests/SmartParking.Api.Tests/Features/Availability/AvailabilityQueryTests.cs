using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Features.Availability;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure;
using SmartParking.ProjectionRebuilder;
using SmartParking.Tests.Common;
using static SmartParking.Api.Tests.Fixtures.AvailabilityApi;
using static SmartParking.Api.Tests.Fixtures.AvailabilityView;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Availability;

/// <summary>
/// <c>GET /api/availability</c> (spec 004, AC-13 to AC-15), the CQRS proof that the queries read only Redis (AC-19) and
/// the monolithic mode (AC-21). The view is built by the real rebuild in an isolated environment (AC-9's configuration).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class AvailabilityQueryTests(ApiFixture fixture)
{
    [Fact]
    public async Task GetAvailability_FacilityWithTwoOccupied_ReturnsFacilityAndZoneCounts_AC13()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        using var client = factory.CreateClient();
        var state = await ArrangeAc9WithTwoOccupiedAsync(environment, client, factory.Services, cancellationToken);

        // Act
        var answer = await GetOkAsync(client, $"facilityId={state.Site.Facility.Id}", cancellationToken);

        // Assert
        var facility = Assert.Single(answer.Facilities);
        Assert.Equal((state.Site.Facility.Id, state.Site.Facility.Name, Operator.ReferenceOperatorId, 2, 4),
            (facility.FacilityId, facility.Name, facility.OperatorId, facility.Free, facility.Total));
        Assert.Equal(
            new Dictionary<Guid, (int, int)> { [state.Site.Zone.Id] = (1, 2), [state.Zone2Id] = (1, 2) },
            facility.Zones.ToDictionary(zone => zone.ZoneId, zone => (zone.Free, zone.Total)));
    }

    [Theory]
    [InlineData("spotType=Accessible")]
    [InlineData("spotType=Standard")]
    [InlineData("zoneId")]
    [InlineData("operatorId")]
    [InlineData("facilityId+otherOperatorId")]
    [InlineData("operatorWithoutFacilities")]
    public async Task GetAvailability_Filters_FollowTheContract_AC14(string filter)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        using var client = factory.CreateClient();
        var state = await ArrangeAc9WithTwoOccupiedAsync(environment, client, factory.Services, cancellationToken);
        var facilityId = state.Site.Facility.Id;
        var (zone1, zone2) = (state.Site.Zone.Id, state.Zone2Id);

        // Expected (facility free/total, zones free/total) after AC-9 with zone 1 Standard and zone 2 Accessible occupied.
        var (query, expected) = filter switch
        {
            "spotType=Accessible" => ($"facilityId={facilityId}&spotType=Accessible",
                Expect(facilityId, (0, 1), (zone1, 0, 0), (zone2, 0, 1))),
            "spotType=Standard" => ($"facilityId={facilityId}&spotType=Standard",
                Expect(facilityId, (2, 3), (zone1, 1, 2), (zone2, 1, 1))),
            "zoneId" => ($"facilityId={facilityId}&zoneId={zone2}",
                Expect(facilityId, (2, 4), (zone2, 1, 2))),
            "operatorId" => ($"operatorId={Operator.ReferenceOperatorId}",
                Expect(facilityId, (2, 4), (zone1, 1, 2), (zone2, 1, 2))),
            "facilityId+otherOperatorId" => ($"facilityId={facilityId}&operatorId={Guid.NewGuid()}", Array.Empty<string>()),
            "operatorWithoutFacilities" => ($"operatorId={Guid.NewGuid()}", Array.Empty<string>()),
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };

        // Act
        var answer = await GetOkAsync(client, query, cancellationToken);

        // Assert
        Assert.Equal(expected, answer.Facilities.Select(Describe));
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("zoneId=0193a1b0-0000-7000-8000-000000000009&spotType=Standard", HttpStatusCode.BadRequest)]
    [InlineData("facilityId=0193a1b0-0000-7000-8000-000000000009&spotType=Car", HttpStatusCode.BadRequest)]
    [InlineData("facilityId=0193a1b0-0000-7000-8000-000000000009&spotType=standard", HttpStatusCode.BadRequest)]
    [InlineData("facilityId={new}", HttpStatusCode.NotFound)]
    public async Task GetAvailability_InvalidOrUnknown_Returns400Or404_AC15(string query, HttpStatusCode expected)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await GetAsync(client, query.Replace("{new}", Guid.NewGuid().ToString(), StringComparison.Ordinal), cancellationToken);

        // Assert
        var problem = await AssertProblemAsync(response, expected, cancellationToken);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.True(problem.TryGetProperty("errors", out _), "A 400 must be ValidationProblemDetails.");
        }
        else
        {
            Assert.Equal("Availability.FacilityNotFound", problem.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task Queries_PostgresAndMongoUnreachable_Return200FromRedis_AC19()
    {
        // Arrange: PostgreSQL and MongoDB are closed ports; the view is written by the host itself, without PostgreSQL.
        var cancellationToken = TestContext.Current.CancellationToken;
        var unreachable = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Postgres"] = unreachable["ConnectionStrings:Postgres"];
        settings["ConnectionStrings:Mongo"] = unreachable["ConnectionStrings:Mongo"];
        settings["ConnectionStrings:Redis"] = fixture.Databases.Redis.GetConnectionString() + $",defaultDatabase={IsolatedRedisDatabase}";
        await using var factory = ApiFixture.CreateFactory(settings);
        using var client = factory.CreateClient();

        var (facilityId, zoneId, operatorId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var store = factory.Services.GetRequiredService<IAvailabilityViewStore>();
        await store.ClearAsync(cancellationToken);
        await store.SeedAsync(
            new AvailabilityLayout(
            [
                new FacilityLayout(facilityId, "Only Redis", operatorId, 40.2033, -8.4103,
                    [new ZoneLayout(zoneId, "Zone A")],
                    [new SpotLayout(Guid.NewGuid(), zoneId, SpotType.Standard), new SpotLayout(Guid.NewGuid(), zoneId, SpotType.Accessible)]),
            ]),
            cancellationToken);

        // Act
        var byFacility = await GetOkAsync(client, $"facilityId={facilityId}", cancellationToken);
        var nearby = await NearbyOkAsync(client, "lat=40.2033&lon=-8.4103&radiusMeters=100", cancellationToken);

        // Assert
        var facility = Assert.Single(byFacility.Facilities);
        Assert.Equal((facilityId, "Only Redis", operatorId, 2, 2), (facility.FacilityId, facility.Name, facility.OperatorId, facility.Free, facility.Total));
        Assert.Equal([(zoneId, "Zone A", 2, 2)], facility.Zones.Select(zone => (zone.ZoneId, zone.Name, zone.Free, zone.Total)));
        var found = Assert.Single(nearby.Facilities);
        Assert.Equal((facilityId, 2, 2), (found.FacilityId, found.Free, found.Total));
    }

    [Theory]
    [InlineData("/api/availability?facilityId=0193a1b0-0000-7000-8000-000000000009")]
    [InlineData("/api/availability")]
    [InlineData("/api/availability/nearby?lat=40.2&lon=-8.4&radiusMeters=1000")]
    [InlineData("/api/availability/nearby?lat=95")]
    public async Task Queries_MonolithicMode_Return501_AC21(string route)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = fixture.CreateFactory(PersistenceMode.Monolithic);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri(route, UriKind.Relative), cancellationToken);

        // Assert: the mode is checked before validation, so even an invalid request is a 501 (nothing is read).
        var problem = await AssertProblemAsync(response, HttpStatusCode.NotImplemented, cancellationToken);
        Assert.Equal("Availability.NotImplementedInMode", problem.GetProperty("title").GetString());
    }

    /// <summary>
    /// AC-9's configuration (zone 1: two active Standard spots and an inactive one; zone 2: one Accessible and one
    /// Standard; an inactive facility), rebuilt, then one Standard spot of zone 1 and the Accessible spot occupied.
    /// </summary>
    private static async Task<Ac9State> ArrangeAc9WithTwoOccupiedAsync(
        Isolated environment, HttpClient client, IServiceProvider services, CancellationToken cancellationToken)
    {
        var site = await CreateSiteAsync(client, 3, cancellationToken);
        var (zone2, _) = await ConfigurationApi.CreateZoneAsync(client, site.Facility.Id, cancellationToken);
        var accessible = await CreateTypedSpotAsync(client, site.Facility.Id, zone2.Id, "Accessible", cancellationToken);
        await CreateTypedSpotAsync(client, site.Facility.Id, zone2.Id, "Standard", cancellationToken);
        await UpdateSpotAsync(client, site.Facility.Id, site.Spots[2].SpotId, cancellationToken, status: "Inactive");
        var inactiveSite = await CreateSiteAsync(client, 1, cancellationToken);
        await DeactivateFacilityAsync(client, inactiveSite, cancellationToken);

        Assert.Equal(ExitCodes.Success, await RunRebuilderAsync(environment, cancellationToken));
        Assert.Equal(ProjectionOutcome.Applied, await OccupyAsync(services, site, site.Spots[0], cancellationToken));
        Assert.Equal(ProjectionOutcome.Applied, await OccupyAsync(services, site, new SpotWithSensor(accessible, Guid.NewGuid(), "HW-accessible"), cancellationToken));
        return new Ac9State(site, zone2.Id);
    }

    private static async Task<Guid> CreateTypedSpotAsync(HttpClient client, Guid facilityId, Guid zoneId, string type, CancellationToken cancellationToken)
    {
        var (spot, _) = await ConfigurationApi.PostAsync<SmartParking.Api.Features.Spots.SpotResponse>(
            client, ConfigurationApi.Spots(facilityId), new SmartParking.Api.Features.Spots.CreateSpotRequest(zoneId, ConfigurationApi.NewCode(), type), cancellationToken);
        return spot.Id;
    }

    private static string[] Expect(Guid facilityId, (int Free, int Total) facility, params (Guid ZoneId, int Free, int Total)[] zones) =>
        [$"{facilityId} {facility.Free}/{facility.Total} [{string.Join(", ", zones.OrderBy(zone => zone.ZoneId).Select(zone => $"{zone.ZoneId} {zone.Free}/{zone.Total}"))}]"];

    private static string Describe(FacilityAvailabilityResponse facility) =>
        $"{facility.FacilityId} {facility.Free}/{facility.Total} [{string.Join(", ", facility.Zones.OrderBy(zone => zone.ZoneId).Select(zone => $"{zone.ZoneId} {zone.Free}/{zone.Total}"))}]";

    private sealed record Ac9State(Site Site, Guid Zone2Id);
}
