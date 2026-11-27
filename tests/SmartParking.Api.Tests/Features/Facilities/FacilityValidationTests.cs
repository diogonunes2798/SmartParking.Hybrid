using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Facilities;

/// <summary>
/// Invalid facility requests are answered with 400 ValidationProblemDetails before the handler runs (spec 001, AC-17).
/// These are the facility cases; the tariff cases (negative price, zero billing increment) are in
/// <c>Tariffs/TariffTests.Create_InvalidInput_Returns400ValidationProblemAndWritesNothing_AC17</c>.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class FacilityValidationTests(ApiFixture fixture)
{
    public static TheoryData<string, string> InvalidRequests => new()
    {
        { "latitude", "latitude out of range" },
        { "timeZone", "unknown time zone" },
        { "openingHours[0].opensAt", "opensAt not before closesAt" },
        { "openingHours", "repeated day" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Create_InvalidInput_Returns400ValidationProblemAndWritesNothing_AC17(string field, string scenario)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var request = Invalid(scenario);

        // Act
        using var response = await client.PostAsJsonAsync(FacilityApi.Route, request, cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for '{field}'.");

        var written = await DbProbe.ScalarAsync<long>(
            fixture.Databases.OwnerConnectionString,
            "SELECT count(*) FROM parking_facilities WHERE name = $1",
            cancellationToken,
            request.Name!);
        Assert.Equal(0, written);
    }

    private static CreateFacilityRequest Invalid(string scenario)
    {
        var valid = FacilityApi.NewRequest();
        return scenario switch
        {
            "latitude out of range" => valid with { Latitude = 85.1 },
            "unknown time zone" => valid with { TimeZone = "Europe/Atlantis" },
            "opensAt not before closesAt" => valid with { OpeningHours = [new OpeningHoursRequest(1, new TimeOnly(20, 0), new TimeOnly(8, 0))] },
            "repeated day" => valid with
            {
                OpeningHours =
                [
                    new OpeningHoursRequest(2, new TimeOnly(8, 0), new TimeOnly(12, 0)),
                    new OpeningHoursRequest(2, new TimeOnly(14, 0), new TimeOnly(18, 0)),
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
        };
    }
}
