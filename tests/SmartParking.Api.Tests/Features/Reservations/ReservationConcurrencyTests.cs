using System.Net;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Reservations;

/// <summary>
/// AC-27 (spec 002): a cancellation and a fulfilment of the same reservation write over the same <c>xmin</c>. The
/// <see cref="ConcurrencyGate"/> holds both requests at the unit of work until both have read the reservation, so the
/// second save meets a stale <c>xmin</c> for certain (plan 002b). The gate is disarmed while the data is arranged.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class ReservationConcurrencyTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task CancelAndFulfil_ConcurrentOnSameXmin_OneSucceedsOne409SingleReservationEvent_AC27()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Gated.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var reservation = await ReservationApi.CreateOkAsync(client, ReservationApi.Request(arranged), cancellationToken);
        fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart);

        // Act
        var responses = await fixture.Gate.RaceAsync(
            () => ReservationApi.CancelAsync(client, reservation.Id, cancellationToken),
            () => SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken, reservation.Id));

        // Assert
        using var cancel = responses[0];
        using var open = responses[1];
        var cancelWon = cancel.IsSuccessStatusCode;
        Assert.Equal(HttpStatusCode.Conflict, cancelWon ? open.StatusCode : cancel.StatusCode);
        Assert.Equal(cancelWon ? HttpStatusCode.OK : HttpStatusCode.Created, cancelWon ? cancel.StatusCode : open.StatusCode);
        var loser = cancelWon ? open : cancel;
        Assert.Equal("application/problem+json", loser.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            cancelWon ? "Session.ConcurrentChange" : "Reservation.ConcurrentChange",
            await loser.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);

        Assert.Equal(cancelWon ? "Cancelled" : "Fulfilled", await ReservationApi.StatusAsync(App, reservation.Id, cancellationToken));
        Assert.Equal(
            ["ReservationCreated.v1", cancelWon ? "ReservationCancelled.v1" : "SessionOpened.v1"],
            await ReservationApi.OutboxTypesAsync(App, reservation.Id, cancellationToken));
        Assert.Equal(cancelWon ? 0 : 1, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM parking_sessions WHERE vehicle_id = $1", cancellationToken, arranged.VehicleId));
    }
}
