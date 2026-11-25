using System.Net;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>
/// AC-14 (spec 002): the gateway approves and the save fails. Nothing is written, the session stays Closed, and a
/// retry with the same payment method succeeds without a second charge. The failing trigger lives in an isolated
/// database, so it cannot break the other tests (as AC-8 of spec 001).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class PaymentAtomicityTests(ApiFixture fixture)
{
    private const string CreateTrigger =
        """
        CREATE FUNCTION fail_outbox_insert() RETURNS trigger LANGUAGE plpgsql AS
        'BEGIN RAISE EXCEPTION ''forced outbox failure (AC-14)''; END';
        CREATE TRIGGER outbox_insert_fails BEFORE INSERT ON outbox_messages
            FOR EACH ROW EXECUTE FUNCTION fail_outbox_insert();
        """;

    private const string CountWrites =
        """
        SELECT (SELECT count(*) FROM payments) * 1000000
             + (SELECT count(*) FROM audit_entries) * 1000
             + (SELECT count(*) FROM outbox_messages)
        """;

    [Fact]
    public async Task Pay_OutboxInsertFails_Returns500AndWritesNothing_AC14()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));
        var database = await fixture.Databases.CreateIsolatedDatabaseAsync(cancellationToken);
        await using var factory = fixture.CreateFactory(database, fixture.UseClock);
        using var client = factory.CreateClient();
        var owner = database.OwnerConnectionString;
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var closed = await SessionApi.ClosedSessionAsync(client, fixture.Clock, arranged, TimeSpan.FromMinutes(40), cancellationToken);
        await DbProbe.ExecuteAsync(owner, CreateTrigger, cancellationToken);
        var writesBefore = await DbProbe.ScalarAsync<long>(owner, CountWrites, cancellationToken);

        // Act
        using var failed = await SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("Closed", await DbProbe.ScalarAsync<string>(owner, "SELECT status FROM parking_sessions WHERE id = $1", cancellationToken, closed.Id));
        Assert.Equal(writesBefore, await DbProbe.ScalarAsync<long>(owner, CountWrites, cancellationToken));
        Assert.Equal(1, SessionApi.ChargeCount(factory.Services, closed.Id, arranged.PaymentMethodId));

        // Act: the same payment method again, once the failure is gone.
        await DbProbe.ExecuteAsync(owner, "DROP TRIGGER outbox_insert_fails ON outbox_messages", cancellationToken);
        using var retried = await SessionApi.PayAsync(client, closed.Id, arranged.PaymentMethodId, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("Paid", await DbProbe.ScalarAsync<string>(owner, "SELECT status FROM parking_sessions WHERE id = $1", cancellationToken, closed.Id));
        Assert.Equal(1, SessionApi.ChargeCount(factory.Services, closed.Id, arranged.PaymentMethodId));
    }
}
