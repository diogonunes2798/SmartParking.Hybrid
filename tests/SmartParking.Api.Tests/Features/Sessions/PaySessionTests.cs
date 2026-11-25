using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Features.Sessions;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>AC-12, AC-13, AC-15, AC-17 and AC-18 (spec 002): paying a closed session through the simulated gateway.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class PaySessionTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Pay_ClosedSession_Returns200PaidOneEventTwoAuditsSameXmin_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, arranged, closed) = await ClosedSessionAsync(TimeSpan.FromMinutes(40), cancellationToken);
        using var _ = client;

        // Act
        using var response = await SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paid = (await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken))!;
        Assert.Equal("Paid", paid.Session.Status);
        Assert.Equal("Succeeded|0.90", await DbProbe.ScalarAsync<string>(
            App, "SELECT status || '|' || amount::text FROM payments WHERE id = $1", cancellationToken, paid.PaymentId));

        var outboxId = await DbProbe.ScalarAsync<Guid>(
            App, "SELECT id FROM outbox_messages WHERE type = 'SessionPaid.v1' AND payload->>'id' = $1", cancellationToken, closed.Id.ToString());
        Assert.Equal(
            ["ParkingSession|Updated", "Payment|Created"],
            await DbProbe.ColumnAsync(
                App, "SELECT entity_type || '|' || operation FROM audit_entries WHERE outbox_message_id = $1 ORDER BY entity_type",
                cancellationToken, outboxId));

        // One transaction: the session, the payment, both audit entries and the message share the xmin of its commit.
        var xmins = await DbProbe.ColumnAsync(
            App,
            """
            SELECT xmin::text FROM parking_sessions WHERE id = $1
            UNION ALL SELECT xmin::text FROM payments WHERE id = $2
            UNION ALL SELECT xmin::text FROM audit_entries WHERE outbox_message_id = $3
            UNION ALL SELECT xmin::text FROM outbox_messages WHERE id = $3
            """,
            cancellationToken, closed.Id, paid.PaymentId, outboxId);
        Assert.Equal(5, xmins.Count);
        Assert.Single(xmins.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, SessionApi.ChargeCount(fixture.Sessions.Services, closed.Id, arranged.PaymentMethodId));
    }

    [Fact]
    public async Task Pay_DeclinedToken_Returns402WithPaymentIdAndStaysClosed_AC13()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, arranged, closed) = await ClosedSessionAsync(TimeSpan.FromMinutes(40), cancellationToken);
        using var _ = client;
        var declining = await UserApi.AddPaymentMethodAsync(client, arranged.UserId, cancellationToken, $"decline_{Guid.NewGuid():N}");

        // Act
        using var response = await SessionApi.PayAsync(client, closed.Id, declining.Id, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var paymentId = problem.RootElement.GetProperty("paymentId").GetGuid();
        Assert.Equal("Failed", await DbProbe.ScalarAsync<string>(App, "SELECT status FROM payments WHERE id = $1", cancellationToken, paymentId));
        Assert.Equal(["Payment|Created|PaymentFailed.v1"], await UserApi.AuditAsync(App, paymentId, cancellationToken));
        Assert.Equal(
            ["SessionOpened.v1", "SessionClosed.v1", "PaymentFailed.v1"],
            await SessionApi.SessionOutboxTypesAsync(App, closed.Id, cancellationToken));
        Assert.Equal("Closed", await SessionStatusAsync(closed.Id, cancellationToken));
    }

    [Fact]
    public async Task Pay_AfterDecline_OtherMethod_Returns200_AC13()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, arranged, closed) = await ClosedSessionAsync(TimeSpan.FromMinutes(40), cancellationToken);
        using var _ = client;
        var declining = await UserApi.AddPaymentMethodAsync(client, arranged.UserId, cancellationToken, $"decline_{Guid.NewGuid():N}");
        using (var declined = await SessionApi.PayAsync(client, closed.Id, declining.Id, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
        }

        // Act
        using var response = await SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Paid", await SessionStatusAsync(closed.Id, cancellationToken));
        Assert.Equal(
            ["Failed", "Succeeded"],
            await DbProbe.ColumnAsync(App, "SELECT status FROM payments WHERE session_id = $1 ORDER BY status", cancellationToken, closed.Id));
    }

    [Fact]
    public async Task PaidSession_CloseOrPayAgain_Returns409NoNewEvents_AC15()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, arranged, closed) = await ClosedSessionAsync(TimeSpan.FromMinutes(40), cancellationToken);
        using var _ = client;
        using (var paid = await SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken))
        {
            paid.EnsureSuccessStatusCode();
        }

        // Act
        using var close = await SessionApi.CloseAsync(client, closed.Id, cancellationToken);
        using var payAgain = await SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, close.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, payAgain.StatusCode);
        Assert.Equal("Paid", await SessionStatusAsync(closed.Id, cancellationToken));
        Assert.Equal(
            ["SessionOpened.v1", "SessionClosed.v1", "SessionPaid.v1"],
            await SessionApi.SessionOutboxTypesAsync(App, closed.Id, cancellationToken));
    }

    [Theory]
    [InlineData("open session")]
    [InlineData("method of another user")]
    public async Task Pay_PreconditionFails_Returns409NoChargeNoWrite_AC17(string precondition)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var opened = await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        var methodId = arranged.PaymentMethodId;
        if (precondition == "method of another user")
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(40));
            await SessionApi.CloseOkAsync(client, opened.Id, cancellationToken);
            methodId = (await SessionApi.OtherDriverAsync(client, cancellationToken)).PaymentMethodId;
        }

        var outboxBefore = await SessionApi.SessionOutboxTypesAsync(App, opened.Id, cancellationToken);

        // Act
        using var response = await SessionApi.PayAsync(client, opened.Id, methodId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, SessionApi.ChargeCount(fixture.Sessions.Services, opened.Id, methodId));
        Assert.Equal(0, await DbProbe.ScalarAsync<long>(App, "SELECT count(*) FROM payments WHERE session_id = $1", cancellationToken, opened.Id));
        Assert.Equal(outboxBefore, await SessionApi.SessionOutboxTypesAsync(App, opened.Id, cancellationToken));
    }

    [Fact]
    public async Task Pay_ZeroAmount_SucceedsWithoutGateway_AC18()
    {
        // Arrange: 10 minutes are within the 15 free minutes.
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, arranged, closed) = await ClosedSessionAsync(TimeSpan.FromMinutes(10), cancellationToken);
        using var _ = client;
        Assert.Equal(0.00m, closed.Amount);

        // Act
        using var response = await SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, SessionApi.ChargeCount(fixture.Sessions.Services, closed.Id, arranged.PaymentMethodId));
        Assert.Equal("Succeeded|0.00", await DbProbe.ScalarAsync<string>(
            App, "SELECT status || '|' || amount::text FROM payments WHERE session_id = $1", cancellationToken, closed.Id));
        Assert.Equal("Paid", await SessionStatusAsync(closed.Id, cancellationToken));
        Assert.Contains("SessionPaid.v1", await SessionApi.SessionOutboxTypesAsync(App, closed.Id, cancellationToken));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("payment method")]
    public async Task Pay_UnknownSessionOrMethod_Returns404(string unknown)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, arranged, closed) = await ClosedSessionAsync(TimeSpan.FromMinutes(40), cancellationToken);
        using var _ = client;

        // Act
        using var response = await SessionApi.PayAsync(
            client,
            unknown == "session" ? Guid.CreateVersion7() : closed.Id,
            unknown == "payment method" ? Guid.CreateVersion7() : arranged.PaymentMethodId,
            cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(HttpClient Client, SessionApi.Arranged Arranged, SessionResponse Closed)> ClosedSessionAsync(
        TimeSpan duration, CancellationToken cancellationToken)
    {
        fixture.Clock.SetUtcNow(Wednesday10);
        var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var closed = await SessionApi.ClosedSessionAsync(client, fixture.Clock, arranged, duration, cancellationToken);
        return (client, arranged, closed);
    }

    private Task<string> SessionStatusAsync(Guid sessionId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<string>(App, "SELECT status FROM parking_sessions WHERE id = $1", cancellationToken, sessionId);
}
