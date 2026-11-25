using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Features.Reservations;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Reservations;

/// <summary>AC-22 and AC-23 (spec 002): creating a reservation and its rules.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class CreateReservationTests(ApiFixture fixture)
{
    /// <summary>Wednesday 10:00 in Lisbon (UTC+0 in December), one hour before the reference reservation.</summary>
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Opening hours on Wednesday only (dayOfWeek 3): Thursday has no row.</summary>
    private static readonly OpeningHoursRequest WednesdayOnly = new(3, new TimeOnly(8, 0), new TimeOnly(20, 0));

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Create_Valid_Returns201ActiveOneAuditOneEvent_AC22()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);

        // Act
        using var response = await ReservationApi.CreateAsync(client, ReservationApi.Request(arranged), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reservation = (await response.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken))!;
        Assert.Equal($"/api/reservations/{reservation.Id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal("Active", reservation.Status);
        Assert.Equal(ReservationApi.ReferenceStart, reservation.StartsAt);
        Assert.Equal(ReservationApi.ReferenceEnd, reservation.EndsAt);
        Assert.Equal("Active", await ReservationApi.StatusAsync(App, reservation.Id, cancellationToken));
        Assert.Equal(["Reservation|Created|ReservationCreated.v1"], await UserApi.AuditAsync(App, reservation.Id, cancellationToken));
        Assert.Equal(["ReservationCreated.v1"], await ReservationApi.OutboxTypesAsync(App, reservation.Id, cancellationToken));
    }

    [Theory]
    [InlineData("starts in the past", "Reservation.StartsInPast")]
    [InlineData("longer than 24 h", "Reservation.TooLong")]
    [InlineData("more than 7 days ahead", "Reservation.TooFarAhead")]
    [InlineData("starts before opening", "Reservation.OutsideOpeningHours")]
    [InlineData("ends after closing", "Reservation.OutsideOpeningHours")]
    [InlineData("crosses midnight", "Reservation.OutsideOpeningHours")]
    [InlineData("day without opening hours", "Reservation.OutsideOpeningHours")]
    public async Task Create_RuleBroken_Returns400AndWritesNothing_AC23(string rule, string expectedCode)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(
            client, fixture.Clock, cancellationToken, rule == "day without opening hours" ? [WednesdayOnly] : null);
        var day = new DateTimeOffset(Wednesday10.Date, TimeSpan.Zero);
        var (startsAt, endsAt) = rule switch
        {
            "starts in the past" => (Wednesday10.AddMinutes(-1), Wednesday10.AddMinutes(30)),
            "longer than 24 h" => (ReservationApi.ReferenceStart, ReservationApi.ReferenceStart.AddHours(24).AddMinutes(1)),
            "more than 7 days ahead" => (Wednesday10.AddDays(7).AddMinutes(1), Wednesday10.AddDays(7).AddMinutes(61)),
            "starts before opening" => (day.AddDays(1).AddHours(7.5), day.AddDays(1).AddHours(9)),
            "ends after closing" => (day.AddHours(19.5), day.AddHours(20.5)),
            "crosses midnight" => (day.AddHours(19), day.AddDays(1).AddHours(9)),
            "day without opening hours" => (day.AddDays(1).AddHours(10), day.AddDays(1).AddHours(11)),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };

        // Act
        using var response = await ReservationApi.CreateAsync(
            client, ReservationApi.Request(arranged, startsAt, endsAt), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedCode, await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        await AssertNothingWrittenAsync(arranged.VehicleId, cancellationToken);
    }

    [Theory]
    [InlineData("spot inactive", "Reservation.SpotInactive")]
    [InlineData("facility inactive", "Reservation.FacilityInactive")]
    [InlineData("vehicle of another user", "Reservation.VehicleOfAnotherUser")]
    [InlineData("overlaps an active reservation", "Reservation.Overlaps")]
    public async Task Create_ConflictRule_Returns409AndWritesNothing_AC23(string rule, string expectedCode)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var other = await SessionApi.OtherDriverAsync(client, cancellationToken);
        var request = ReservationApi.Request(arranged);

        switch (rule)
        {
            case "spot inactive":
                await DeactivateSpotAsync(client, arranged, cancellationToken);
                break;

            case "facility inactive":
                await DeactivateFacilityAsync(client, arranged, cancellationToken);
                break;

            case "vehicle of another user":
                request = request with { VehicleId = other.VehicleId };
                break;

            default:
                // Another driver holds 10:30–11:30 on the same spot; the request (11:00–12:00) overlaps it.
                await ReservationApi.CreateOkAsync(
                    client,
                    new CreateReservationRequest(arranged.SpotId, other.UserId, other.VehicleId, Wednesday10.AddMinutes(30), Wednesday10.AddMinutes(90)),
                    cancellationToken);
                break;
        }

        // Act
        using var response = await ReservationApi.CreateAsync(client, request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedCode, await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        await AssertNothingWrittenAsync(request.VehicleId!.Value, cancellationToken);
    }

    [Fact]
    public async Task Create_EndsAtClosingTime_Returns201()
    {
        // Arrange ([startsAt, endsAt) is half-open: a reservation may end exactly at 20:00)
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var closing = new DateTimeOffset(2026, 12, 2, 20, 0, 0, TimeSpan.Zero);

        // Act
        using var response = await ReservationApi.CreateAsync(
            client, ReservationApi.Request(arranged, closing.AddHours(-1), closing), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_OverlapsCancelledReservation_Returns201()
    {
        // Arrange (the EXCLUDE constraint only counts Active reservations)
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var first = await ReservationApi.CreateOkAsync(client, ReservationApi.Request(arranged), cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var cancelled = await ReservationApi.CancelAsync(client, first.Id, cancellationToken))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        using var response = await ReservationApi.CreateAsync(client, ReservationApi.Request(arranged), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("spot")]
    [InlineData("user")]
    [InlineData("vehicle")]
    public async Task Create_UnknownSpotUserOrVehicle_Returns404(string unknown)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var request = unknown switch
        {
            "spot" => ReservationApi.Request(arranged) with { SpotId = Guid.CreateVersion7() },
            "user" => ReservationApi.Request(arranged) with { UserId = Guid.CreateVersion7() },
            _ => ReservationApi.Request(arranged) with { VehicleId = Guid.CreateVersion7() },
        };

        // Act
        using var response = await ReservationApi.CreateAsync(client, request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_EndsBeforeItStarts_Returns400FromValidation()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);

        // Act
        using var response = await ReservationApi.CreateAsync(
            client, ReservationApi.Request(arranged, ReservationApi.ReferenceEnd, ReservationApi.ReferenceStart), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("endsAt", await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
    }

    private static async Task DeactivateSpotAsync(HttpClient client, SessionApi.Arranged arranged, CancellationToken cancellationToken)
    {
        var spot = await client.GetFromJsonAsync<SpotResponse>(
            $"{ConfigurationApi.Spots(arranged.Facility.Id)}/{arranged.SpotId}", cancellationToken);
        using var response = await ConfigurationApi.SendAsync(
            client,
            HttpMethod.Put,
            $"{ConfigurationApi.Spots(arranged.Facility.Id)}/{arranged.SpotId}",
            arranged.SpotETag,
            cancellationToken,
            new UpdateSpotRequest(arranged.ZoneId, spot!.Code, "Standard", "Inactive"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeactivateFacilityAsync(HttpClient client, SessionApi.Arranged arranged, CancellationToken cancellationToken)
    {
        var facility = arranged.Facility;
        using var response = await ConfigurationApi.SendAsync(
            client,
            HttpMethod.Put,
            $"{FacilityApi.Route}/{facility.Id}",
            arranged.FacilityETag,
            cancellationToken,
            new UpdateFacilityRequest(
                facility.Name, facility.Address, facility.Latitude, facility.Longitude, facility.TimeZone, "Inactive",
                [.. facility.OpeningHours.Select(hours => new OpeningHoursRequest(hours.DayOfWeek, hours.OpensAt, hours.ClosesAt))]));
        response.EnsureSuccessStatusCode();
    }

    private async Task AssertNothingWrittenAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        Assert.Equal(0, await ReservationApi.CountAsync(App, vehicleId, cancellationToken));
        Assert.Equal(0, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM audit_entries WHERE entity_type = 'Reservation' AND after->>'vehicleId' = $1",
            cancellationToken, vehicleId.ToString()));
        Assert.Equal(0, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM outbox_messages WHERE type = 'ReservationCreated.v1' AND payload->>'vehicleId' = $1",
            cancellationToken, vehicleId.ToString()));
    }
}
