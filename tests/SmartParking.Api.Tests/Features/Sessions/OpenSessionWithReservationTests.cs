using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Sessions;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>
/// AC-25 and AC-26 (spec 002): fulfilling a reservation by opening a session, and the protection of a reserved spot.
/// The reference reservation is 11:00–12:00, so its fulfilment window is [10:45, 11:15], closed at both ends.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class OpenSessionWithReservationTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset WindowStart = ReservationApi.ReferenceStart.AddMinutes(-15);

    private static readonly DateTimeOffset WindowEnd = ReservationApi.ReferenceStart.AddMinutes(15);

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Open_WithReservationInWindow_Returns201FulfilledOneEventTwoAudits_AC25()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, reservationId) = await ArrangeReservationAsync(client, cancellationToken);
        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart.AddMinutes(-10));

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken, reservationId);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken))!;
        Assert.Equal(reservationId, session.ReservationId);
        Assert.Equal("Fulfilled", await ReservationApi.StatusAsync(App, reservationId, cancellationToken));
        Assert.Equal(["ReservationCreated.v1", "SessionOpened.v1"], await ReservationApi.OutboxTypesAsync(App, reservationId, cancellationToken));
        Assert.Equal(["SessionOpened.v1"], await SessionApi.SessionOutboxTypesAsync(App, session.Id, cancellationToken));
        Assert.Equal(
            ["Reservation|Created|ReservationCreated.v1", "Reservation|Updated|SessionOpened.v1"],
            await UserApi.AuditAsync(App, reservationId, cancellationToken));
        Assert.Equal(["ParkingSession|Created|SessionOpened.v1"], await UserApi.AuditAsync(App, session.Id, cancellationToken));

        // The two audit entries point to the same message and share its xmin, and the reservation's: one transaction.
        var sameTransaction = await DbProbe.ColumnAsync(
            App,
            """
            SELECT a.entity_type FROM audit_entries a JOIN outbox_messages o ON o.id = a.outbox_message_id
            WHERE o.type = 'SessionOpened.v1' AND o.payload->>'id' = $1
              AND a.xmin = o.xmin AND o.xmin = (SELECT xmin FROM reservations WHERE id = $2)
            ORDER BY a.entity_type
            """,
            cancellationToken,
            session.Id.ToString(),
            reservationId);
        Assert.Equal(["ParkingSession", "Reservation"], sameTransaction);
    }

    [Theory]
    [InlineData("1 us before the early-arrival margin", "Session.OutsideFulfilmentWindow")]
    [InlineData("1 us after the grace period", "Session.OutsideFulfilmentWindow")]
    [InlineData("another vehicle", "Session.ReservationMismatch")]
    [InlineData("another spot", "Session.ReservationMismatch")]
    public async Task Open_WithReservation_Fails409AndWritesNothing_AC25(string condition, string expectedCode)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, reservationId) = await ArrangeReservationAsync(client, cancellationToken);
        var spotId = arranged.SpotId;
        var vehicleId = arranged.VehicleId;
        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart);

        switch (condition)
        {
            case "1 us before the early-arrival margin":
                fixture.Clock.SetUtcNow(WindowStart.AddMicroseconds(-1));
                break;

            case "1 us after the grace period":
                fixture.Clock.SetUtcNow(WindowEnd.AddMicroseconds(1));
                break;

            case "another vehicle":
                vehicleId = (await SessionApi.OtherDriverAsync(client, cancellationToken)).VehicleId;
                break;

            default:
                spotId = (await ConfigurationApi.CreateSpotAsync(client, arranged.Facility.Id, arranged.ZoneId, cancellationToken)).Spot.Id;
                break;
        }

        // Act
        using var response = await SessionApi.OpenAsync(client, spotId, vehicleId, cancellationToken, reservationId);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedCode, await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        await AssertNothingWrittenAsync(reservationId, vehicleId, cancellationToken);
    }

    [Theory]
    [InlineData(-15)]
    [InlineData(15)]
    public async Task Open_WithReservationAtTheExactEdgeOfTheWindow_Returns201(int minutesFromStart)
    {
        // Arrange (the window is closed at both ends, spec 002)
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, reservationId) = await ArrangeReservationAsync(client, cancellationToken);
        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart.AddMinutes(minutesFromStart));

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken, reservationId);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Fulfilled", await ReservationApi.StatusAsync(App, reservationId, cancellationToken));
    }

    [Fact]
    public async Task Open_WithCancelledReservation_Returns409()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, reservationId) = await ArrangeReservationAsync(client, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var cancelled = await ReservationApi.CancelAsync(client, reservationId, cancellationToken))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart);

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken, reservationId);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Session.ReservationNotActive", await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        Assert.Equal("Cancelled", await ReservationApi.StatusAsync(App, reservationId, cancellationToken));
    }

    [Fact]
    public async Task Open_WithUnknownReservation_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, _) = await ArrangeReservationAsync(client, cancellationToken);
        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart);

        // Act
        using var response = await SessionApi.OpenAsync(
            client, arranged.SpotId, arranged.VehicleId, cancellationToken, Guid.CreateVersion7());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Reservation.NotFound", await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_OtherVehicleInWindow_Returns409_AC26()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, reservationId) = await ArrangeReservationAsync(client, cancellationToken);
        var other = await SessionApi.OtherDriverAsync(client, cancellationToken);
        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart.AddMinutes(5));

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, other.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Session.SpotReserved", await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        Assert.Equal(0, await CountSessionsAsync(other.VehicleId, cancellationToken));
        Assert.Equal("Active", await ReservationApi.StatusAsync(App, reservationId, cancellationToken));
    }

    [Fact]
    public async Task Open_OtherVehicleAfterGrace_Returns201_AC26()
    {
        // Arrange (the reservation is still Active: the worker has not run, but the spot is free again)
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, reservationId) = await ArrangeReservationAsync(client, cancellationToken);
        var other = await SessionApi.OtherDriverAsync(client, cancellationToken);
        fixture.Clock.SetUtcNow(WindowEnd.AddMicroseconds(1));

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, other.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Active", await ReservationApi.StatusAsync(App, reservationId, cancellationToken));
    }

    [Fact]
    public async Task Open_OtherVehicleAtTheEarlyArrivalMargin_Returns409()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, _) = await ArrangeReservationAsync(client, cancellationToken);
        var other = await SessionApi.OtherDriverAsync(client, cancellationToken);
        fixture.Clock.SetUtcNow(WindowStart);

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, other.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Open_SameVehicleWithoutReservationInWindow_Returns201AndKeepsReservationActive()
    {
        // Arrange (plan 002b, point 2: the protection is against another vehicle; the reservation is not fulfilled)
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var (arranged, reservationId) = await ArrangeReservationAsync(client, cancellationToken);
        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart);

        // Act
        using var response = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Active", await ReservationApi.StatusAsync(App, reservationId, cancellationToken));
    }

    /// <summary>The reference spot and a reservation of its vehicle for 11:00–12:00, created at 10:00.</summary>
    private async Task<(SessionApi.Arranged Arranged, Guid ReservationId)> ArrangeReservationAsync(
        HttpClient client, CancellationToken cancellationToken)
    {
        fixture.Clock.SetUtcNow(Wednesday10);
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var reservation = await ReservationApi.CreateOkAsync(client, ReservationApi.Request(arranged), cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        return (arranged, reservation.Id);
    }

    private Task<long> CountSessionsAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<long>(App, "SELECT count(*) FROM parking_sessions WHERE vehicle_id = $1", cancellationToken, vehicleId);

    private async Task AssertNothingWrittenAsync(Guid reservationId, Guid vehicleId, CancellationToken cancellationToken)
    {
        Assert.Equal("Active", await ReservationApi.StatusAsync(App, reservationId, cancellationToken));
        Assert.Equal(["ReservationCreated.v1"], await ReservationApi.OutboxTypesAsync(App, reservationId, cancellationToken));
        Assert.Equal(["Reservation|Created|ReservationCreated.v1"], await UserApi.AuditAsync(App, reservationId, cancellationToken));
        Assert.Equal(0, await CountSessionsAsync(vehicleId, cancellationToken));
    }
}
