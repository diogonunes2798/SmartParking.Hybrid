using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Features.Sessions;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>AC-5 to AC-7 (spec 002): opening a session and its preconditions.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class OpenSessionTests(ApiFixture fixture)
{
    /// <summary>Wednesday 10:00 in Lisbon (UTC+0 in December), inside the 08:00–20:00 hours of the arranged facility.</summary>
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Opening hours on Wednesday only (dayOfWeek 3): Thursday has no row.</summary>
    private static readonly OpeningHoursRequest WednesdayOnly = new(3, new TimeOnly(8, 0), new TimeOnly(20, 0));

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Open_ValidSpot_Returns201OpenWithCurrentTariffOneAuditOneEvent_AC5()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken))!;
        Assert.Equal($"/api/sessions/{session.Id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal("Open", session.Status);
        Assert.Equal(Wednesday10.AddMinutes(5), session.StartedAt);
        Assert.Equal(arranged.TariffId, session.TariffId);
        Assert.Null(session.Amount);
        Assert.Null(session.EndedAt);
        Assert.Equal(["ParkingSession|Created|SessionOpened.v1"], await UserApi.AuditAsync(App, session.Id, cancellationToken));
        Assert.Equal(["SessionOpened.v1"], await SessionApi.SessionOutboxTypesAsync(App, session.Id, cancellationToken));
    }

    [Theory]
    [InlineData("no tariff in force")]
    [InlineData("spot inactive")]
    [InlineData("facility inactive")]
    [InlineData("outside opening hours")]
    [InlineData("day without opening hours")]
    public async Task Open_PreconditionFails_Returns409AndWritesNothing_AC6(string precondition)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(
            client, fixture.Clock, cancellationToken, precondition == "day without opening hours" ? [WednesdayOnly] : null);
        await BreakAsync(client, arranged, precondition, cancellationToken);

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertNothingWrittenAsync(arranged, cancellationToken);
    }

    [Fact]
    public async Task Open_AtClosingTime_Returns409()
    {
        // Arrange (decision 4 of plan 002a: [opensAt, closesAt), so 20:00:00 is already closed)
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        fixture.Clock.SetUtcNow(Wednesday10.Date.AddHours(20));

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNothingWrittenAsync(arranged, cancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Open_VehicleAlreadyOpen_Returns409_AC7(bool sameSpot)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        var spotId = sameSpot
            ? arranged.SpotId
            : (await ConfigurationApi.CreateSpotAsync(client, arranged.Facility.Id, arranged.ZoneId, cancellationToken)).Spot.Id;

        // Act
        using var response = await SessionApi.OpenAsync(client, spotId, arranged.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await CountSessionsAsync(arranged.VehicleId, cancellationToken));
    }

    [Fact]
    public async Task Open_OtherVehicleSameSpot_Returns201()
    {
        // Arrange (no uniqueness per spot, spec 002)
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        var other = await SessionApi.OtherDriverAsync(client, cancellationToken);

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, other.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("spot")]
    [InlineData("vehicle")]
    public async Task Open_UnknownSpotOrVehicle_Returns404(string unknown)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);

        // Act
        using var response = await SessionApi.OpenAsync(
            client,
            unknown == "spot" ? Guid.CreateVersion7() : arranged.SpotId,
            unknown == "vehicle" ? Guid.CreateVersion7() : arranged.VehicleId,
            cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task BreakAsync(HttpClient client, SessionApi.Arranged arranged, string precondition, CancellationToken cancellationToken)
    {
        switch (precondition)
        {
            case "no tariff in force":
                // The reference tariff starts at the clock's now; one minute earlier nothing is in force yet.
                fixture.Clock.SetUtcNow(Wednesday10.AddMinutes(-1));
                break;

            case "spot inactive":
                using (var response = await ConfigurationApi.SendAsync(
                    client,
                    HttpMethod.Put,
                    $"{ConfigurationApi.Spots(arranged.Facility.Id)}/{arranged.SpotId}",
                    arranged.SpotETag,
                    cancellationToken,
                    new UpdateSpotRequest(arranged.ZoneId, await SpotCodeAsync(arranged.SpotId, cancellationToken), "Standard", "Inactive")))
                {
                    response.EnsureSuccessStatusCode();
                }

                break;

            case "facility inactive":
                var facility = arranged.Facility;
                using (var response = await ConfigurationApi.SendAsync(
                    client,
                    HttpMethod.Put,
                    $"{FacilityApi.Route}/{facility.Id}",
                    arranged.FacilityETag,
                    cancellationToken,
                    new UpdateFacilityRequest(
                        facility.Name, facility.Address, facility.Latitude, facility.Longitude, facility.TimeZone, "Inactive",
                        [.. facility.OpeningHours.Select(hours => new OpeningHoursRequest(hours.DayOfWeek, hours.OpensAt, hours.ClosesAt))])))
                {
                    response.EnsureSuccessStatusCode();
                }

                break;

            case "outside opening hours":
                fixture.Clock.SetUtcNow(Wednesday10.Date.AddHours(21));
                break;

            case "day without opening hours":
                fixture.Clock.SetUtcNow(Wednesday10.AddDays(1));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(precondition), precondition, "Unknown precondition.");
        }
    }

    private Task<string> SpotCodeAsync(Guid spotId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<string>(App, "SELECT code FROM parking_spots WHERE id = $1", cancellationToken, spotId);

    private Task<long> CountSessionsAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<long>(App, "SELECT count(*) FROM parking_sessions WHERE vehicle_id = $1", cancellationToken, vehicleId);

    private async Task AssertNothingWrittenAsync(SessionApi.Arranged arranged, CancellationToken cancellationToken)
    {
        Assert.Equal(0, await CountSessionsAsync(arranged.VehicleId, cancellationToken));
        Assert.Equal(0, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM audit_entries WHERE entity_type = 'ParkingSession' AND after->>'vehicleId' = $1",
            cancellationToken, arranged.VehicleId.ToString()));
        Assert.Equal(0, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM outbox_messages WHERE type = 'SessionOpened.v1' AND payload->>'vehicleId' = $1",
            cancellationToken, arranged.VehicleId.ToString()));
    }
}
