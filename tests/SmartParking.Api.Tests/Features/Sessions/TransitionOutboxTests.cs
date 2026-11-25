using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>
/// AC-21 (spec 002, F3a part): each transition writes exactly one outbox message, of its type. The rows of one
/// transaction share the <c>xmin</c> of its commit, so the messages counted are those with the <c>xmin</c> of the row
/// the transition changed (the session; the failed payment for a decline). The reservation transitions (F3b) count by the
/// <c>xmin</c> of the reservation row; expiry is tested in the Workers (<c>ReservationExpiryTests</c>).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class TransitionOutboxTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Theory]
    [InlineData("open", "SessionOpened.v1")]
    [InlineData("close", "SessionClosed.v1")]
    [InlineData("pay", "SessionPaid.v1")]
    [InlineData("decline", "PaymentFailed.v1")]
    [InlineData("create reservation", "ReservationCreated.v1")]
    [InlineData("cancel reservation", "ReservationCancelled.v1")]
    [InlineData("open with reservation", "SessionOpened.v1")]
    public async Task Transition_WritesExactlyOneOutboxMessage_AC21(string transition, string expectedType)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var declining = await UserApi.AddPaymentMethodAsync(client, arranged.UserId, cancellationToken, $"decline_{Guid.NewGuid():N}");

        // Act
        var (table, rowId) = await RunAsync(client, arranged, declining.Id, transition, cancellationToken);

        // Assert
        var types = await DbProbe.ColumnAsync(
            App,
            $"SELECT type FROM outbox_messages WHERE xmin = (SELECT xmin FROM {table} WHERE id = $1)",
            cancellationToken,
            rowId);
        Assert.Equal([expectedType], types);
    }

    private async Task<(string Table, Guid RowId)> RunAsync(
        HttpClient client, SessionApi.Arranged arranged, Guid decliningMethodId, string transition, CancellationToken cancellationToken)
    {
        if (transition.EndsWith("reservation", StringComparison.Ordinal))
        {
            return ("reservations", await RunReservationAsync(client, arranged, transition, cancellationToken));
        }

        var opened = await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        if (transition == "open")
        {
            return ("parking_sessions", opened.Id);
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(40));
        await SessionApi.CloseOkAsync(client, opened.Id, cancellationToken);
        if (transition == "close")
        {
            return ("parking_sessions", opened.Id);
        }

        if (transition == "pay")
        {
            using var paid = await SessionApi.PayAsync(client, opened.Id, arranged.PaymentMethodId, cancellationToken);
            paid.EnsureSuccessStatusCode();
            return ("parking_sessions", opened.Id);
        }

        using var declined = await SessionApi.PayAsync(client, opened.Id, decliningMethodId, cancellationToken);
        using var problem = JsonDocument.Parse(await declined.Content.ReadAsStringAsync(cancellationToken));
        return ("payments", problem.RootElement.GetProperty("paymentId").GetGuid());
    }

    private async Task<Guid> RunReservationAsync(
        HttpClient client, SessionApi.Arranged arranged, string transition, CancellationToken cancellationToken)
    {
        var created = await ReservationApi.CreateOkAsync(client, ReservationApi.Request(arranged), cancellationToken);
        if (transition == "create reservation")
        {
            return created.Id;
        }

        if (transition == "open with reservation")
        {
            // Counted by the xmin of the reservation row: the fulfilment shares the transaction of the session.
            fixture.Clock.SetUtcNow(ReservationApi.ReferenceStart);
            using var opened = await SessionApi.OpenAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken, created.Id);
            opened.EnsureSuccessStatusCode();
            return created.Id;
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        using var cancelled = await ReservationApi.CancelAsync(client, created.Id, cancellationToken);
        cancelled.EnsureSuccessStatusCode();
        return created.Id;
    }
}
