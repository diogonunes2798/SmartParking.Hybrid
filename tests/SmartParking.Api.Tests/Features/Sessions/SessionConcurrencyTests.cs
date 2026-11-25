using System.Net;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>
/// AC-11 and AC-16 (spec 002): two writes over the same <c>xmin</c>. The <see cref="ConcurrencyGate"/> holds both
/// requests at the unit of work until both have read the session, so the second save meets a stale <c>xmin</c> for
/// certain (plan 002a). The gate is disarmed while the data is arranged.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class SessionConcurrencyTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Close_TwoConcurrentOnSameXmin_One200One409SingleEvent_AC11()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Gated.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var opened = await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(40));
        fixture.Gate.Arm(2);

        // Act
        var responses = await Task.WhenAll(
            SessionApi.CloseAsync(client, opened.Id, cancellationToken),
            SessionApi.CloseAsync(client, opened.Id, cancellationToken));

        // Assert
        var (winner, loser) = Split(responses);
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("application/problem+json", loser.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Session.ConcurrentChange", await loser.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        Assert.Equal(["SessionOpened.v1", "SessionClosed.v1"], await SessionApi.SessionOutboxTypesAsync(App, opened.Id, cancellationToken));
        Dispose(responses);
    }

    [Fact]
    public async Task Pay_TwoConcurrentSameMethod_One200One409OneSucceededOneCharge_AC16()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Gated.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var closed = await SessionApi.ClosedSessionAsync(client, fixture.Clock, arranged, TimeSpan.FromMinutes(40), cancellationToken);
        fixture.Gate.Arm(2);

        // Act
        var responses = await Task.WhenAll(
            SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken),
            SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken));

        // Assert: the loser meets the stale xmin or the unique index of succeeded payments; both are 409 (spec 002).
        var (winner, loser) = Split(responses);
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(1, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM payments WHERE session_id = $1 AND status = 'Succeeded'", cancellationToken, closed.Id));
        Assert.Equal(1, SessionApi.ChargeCount(fixture.Gated.Services, closed.Id, arranged.PaymentMethodId));
        Assert.Equal(
            ["SessionOpened.v1", "SessionClosed.v1", "SessionPaid.v1"],
            await SessionApi.SessionOutboxTypesAsync(App, closed.Id, cancellationToken));
        Dispose(responses);
    }

    [Fact]
    public async Task Open_TwoConcurrentSameVehicle_OneReturns409()
    {
        // Arrange (spec 002, «Edge Cases»: the partial unique index lets only one through)
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Gated.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var (otherSpot, _) = await ConfigurationApi.CreateSpotAsync(client, arranged.Facility.Id, arranged.ZoneId, cancellationToken);
        fixture.Gate.Arm(2);

        // Act
        var responses = await Task.WhenAll(
            SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken),
            SessionApi.OpenAsync(client, otherSpot.Id, arranged.VehicleId, cancellationToken));

        // Assert
        var (winner, loser) = Split(responses);
        Assert.Equal(HttpStatusCode.Created, winner.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(1, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM parking_sessions WHERE vehicle_id = $1", cancellationToken, arranged.VehicleId));
        Dispose(responses);
    }

    private static (HttpResponseMessage Winner, HttpResponseMessage Loser) Split(HttpResponseMessage[] responses)
    {
        var ordered = responses.OrderBy(response => response.IsSuccessStatusCode ? 0 : 1).ToArray();
        return (ordered[0], ordered[1]);
    }

    private static void Dispose(HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
