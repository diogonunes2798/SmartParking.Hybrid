using Microsoft.Extensions.DependencyInjection;
using SmartParking.Tests.Common;
using SmartParking.Workers.Reservations;
using SmartParking.Workers.Tests.Fixtures;

namespace SmartParking.Workers.Tests.Features.Reservations;

/// <summary>
/// AC-28 and the expiry row of AC-21 (spec 002): one pass of the expiry worker, run directly on the <c>Expiry</c> host
/// (fake clock, no timer). The database is shared by the collection, so every assertion is by reservation id.
/// </summary>
[Collection(WorkersCollectionDefinition.Name)]
public sealed class ReservationExpiryTests(WorkersFixture fixture)
{
    /// <summary>Wednesday 2026-12-02 12:00 UTC; the default grace period is 15 minutes.</summary>
    private static readonly DateTimeOffset Now = new(2026, 12, 2, 12, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task Pass_ThreeReservations_ExpiresOnlyTheDueOne_AC28()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        var graph = await ReservationGraph.SeedAsync(App, cancellationToken);
        var due = await graph.InsertReservationAsync(App, "Active", Now.AddMinutes(-60), Now.AddMinutes(-30), cancellationToken);
        var inGrace = await graph.InsertReservationAsync(App, "Active", Now.AddMinutes(-10), Now.AddMinutes(20), cancellationToken);
        var cancelled = await graph.InsertReservationAsync(App, "Cancelled", Now.AddMinutes(-120), Now.AddMinutes(-90), cancellationToken);

        // Act
        var result = await RunPassAsync(cancellationToken);

        // Assert
        Assert.True(result.Expired >= 1);
        Assert.Equal("Expired", await StatusAsync(due, cancellationToken));
        Assert.Equal("Active", await StatusAsync(inGrace, cancellationToken));
        Assert.Equal("Cancelled", await StatusAsync(cancelled, cancellationToken));
        Assert.Equal(["system|Updated|ReservationExpired.v1"], await AuditAsync(due, cancellationToken));
        Assert.Equal(["ReservationExpired.v1"], await OutboxTypesAsync(due, cancellationToken));
        Assert.Empty(await OutboxTypesAsync(inGrace, cancellationToken));
        Assert.Empty(await OutboxTypesAsync(cancelled, cancellationToken));
    }

    [Fact]
    public async Task Pass_XminConflictOnOne_ExpiresTheOthers_AC28()
    {
        // Arrange: three due reservations; another request cancels the middle one between the worker's read and save.
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        var graph = await ReservationGraph.SeedAsync(App, cancellationToken);
        var first = await graph.InsertReservationAsync(App, "Active", Now.AddMinutes(-180), Now.AddMinutes(-150), cancellationToken);
        var conflicted = await graph.InsertReservationAsync(App, "Active", Now.AddMinutes(-120), Now.AddMinutes(-90), cancellationToken);
        var last = await graph.InsertReservationAsync(App, "Active", Now.AddMinutes(-60), Now.AddMinutes(-30), cancellationToken);
        fixture.Hook.Arm(conflicted, token => DbProbe.ExecuteAsync(
            Owner, "UPDATE reservations SET status = 'Cancelled' WHERE id = $1", token, conflicted));

        // Act
        ExpiryPassResult result;
        try
        {
            result = await RunPassAsync(cancellationToken);
        }
        finally
        {
            fixture.Hook.Disarm();
        }

        // Assert
        Assert.Equal(1, result.Conflicts);
        Assert.Equal("Expired", await StatusAsync(first, cancellationToken));
        Assert.Equal("Cancelled", await StatusAsync(conflicted, cancellationToken));
        Assert.Equal("Expired", await StatusAsync(last, cancellationToken));
        Assert.Empty(await OutboxTypesAsync(conflicted, cancellationToken));
        Assert.Empty(await AuditAsync(conflicted, cancellationToken));
        Assert.Equal(["ReservationExpired.v1"], await OutboxTypesAsync(first, cancellationToken));
        Assert.Equal(["ReservationExpired.v1"], await OutboxTypesAsync(last, cancellationToken));
    }

    [Fact]
    public async Task Expire_WritesExactlyOneOutboxMessage_AC21()
    {
        // Arrange (counted by the xmin of the reservation row, as the Api rows of AC-21)
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        var graph = await ReservationGraph.SeedAsync(App, cancellationToken);
        var due = await graph.InsertReservationAsync(App, "Active", Now.AddMinutes(-60), Now.AddMinutes(-30), cancellationToken);

        // Act
        await RunPassAsync(cancellationToken);

        // Assert
        var types = await DbProbe.ColumnAsync(
            App,
            "SELECT type FROM outbox_messages WHERE xmin = (SELECT xmin FROM reservations WHERE id = $1)",
            cancellationToken,
            due);
        Assert.Equal(["ReservationExpired.v1"], types);
    }

    [Fact]
    public async Task Pass_ExactlyAtTheEndOfTheGrace_LeavesItActive()
    {
        // Arrange (due strictly after startsAt + grace, so fulfilment and expiry never overlap)
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        var graph = await ReservationGraph.SeedAsync(App, cancellationToken);
        var atEdge = await graph.InsertReservationAsync(App, "Active", Now.AddMinutes(-15), Now.AddMinutes(15), cancellationToken);

        // Act
        await RunPassAsync(cancellationToken);

        // Assert
        Assert.Equal("Active", await StatusAsync(atEdge, cancellationToken));
    }

    private Task<ExpiryPassResult> RunPassAsync(CancellationToken cancellationToken) =>
        fixture.Expiry.Services.GetRequiredService<ReservationExpiryPass>().RunAsync(cancellationToken);

    private Task<string> StatusAsync(Guid reservationId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<string>(App, "SELECT status FROM reservations WHERE id = $1", cancellationToken, reservationId);

    /// <summary>Audit entries of one reservation: <c>actor|operation|outboxType</c>, oldest first.</summary>
    private Task<List<string>> AuditAsync(Guid reservationId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            App,
            """
            SELECT a.actor || '|' || a.operation || '|' || coalesce(o.type, 'missing')
            FROM audit_entries a LEFT JOIN outbox_messages o ON o.id = a.outbox_message_id
            WHERE a.entity_id = $1
            ORDER BY a.occurred_at, a.id
            """,
            cancellationToken,
            reservationId);

    private Task<List<string>> OutboxTypesAsync(Guid reservationId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            App,
            "SELECT type FROM outbox_messages WHERE payload->>'id' = $1 ORDER BY occurred_at, id",
            cancellationToken,
            reservationId.ToString());
}
