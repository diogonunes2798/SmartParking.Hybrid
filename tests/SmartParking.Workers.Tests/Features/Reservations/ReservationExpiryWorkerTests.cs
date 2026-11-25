using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;
using SmartParking.Workers.Reservations;
using SmartParking.Workers.Tests.Fixtures;

namespace SmartParking.Workers.Tests.Features.Reservations;

/// <summary>The real expiry worker wired into the Workers host (spec 002, «Integrations»; plan 002b).</summary>
[Collection(WorkersCollectionDefinition.Name)]
public sealed class ReservationExpiryWorkerTests(WorkersFixture fixture)
{
    private const string ScanKey = "Reservations:ExpiryScanSeconds";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Worker_Started_ExpiresDueReservation()
    {
        // Arrange: system clock, a pass every second, a reservation one hour past its start.
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = fixture.Databases.AppConnectionString;
        var now = TimeProvider.System.GetUtcNow();
        var graph = await ReservationGraph.SeedAsync(app, cancellationToken);
        var due = await graph.InsertReservationAsync(app, "Active", now.AddHours(-1), now.AddMinutes(-30), cancellationToken);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid, DatabaseContainers.WorkersLogin);
        settings[ScanKey] = "1";
        await using var factory = new ConfiguredFactory<Program>(settings);

        // Act
        using var client = factory.CreateClient();

        // Assert
        var status = await PollAsync(
            () => DbProbe.ScalarAsync<string>(app, "SELECT status FROM reservations WHERE id = $1", cancellationToken, due),
            value => value == "Expired",
            cancellationToken);
        Assert.Equal("Expired", status);
    }

    [Fact]
    public async Task Worker_DatabaseUnreachable_HostKeepsRunning()
    {
        // Arrange: every pass fails to connect; the DbException is logged and the next pass tries again.
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid);
        settings[ScanKey] = "1";
        await using var factory = new ConfiguredFactory<Program>(settings);
        using var client = factory.CreateClient();
        var worker = (BackgroundService)factory.Services.GetServices<IHostedService>().Single(service => service is ReservationExpiryWorker);

        // Act: long enough for the first passes to fail (connection timeout of 2 s).
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        // Assert
        Assert.NotNull(worker.ExecuteTask);
        Assert.False(worker.ExecuteTask.IsCompleted, $"The worker stopped: {worker.ExecuteTask.Exception}");
        await HealthAssertions.AssertLiveAsync(client, cancellationToken);
    }

    [Fact]
    public async Task Worker_SaveFails_HostKeepsRunningAndExpiresOnALaterPass()
    {
        // Arrange (independent review of F3b, finding 1): a trigger makes the expiry save of one reservation fail
        // with an error the unit of work does not translate, so EF Core throws a DbUpdateException out of the pass.
        // The trigger counts its runs in a sequence (nextval is not rolled back), which proves the save did fail.
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = fixture.Databases.AppConnectionString;
        var owner = fixture.Databases.OwnerConnectionString;
        var now = TimeProvider.System.GetUtcNow();
        var graph = await ReservationGraph.SeedAsync(app, cancellationToken);
        var due = await graph.InsertReservationAsync(app, "Active", now.AddHours(-1), now.AddMinutes(-30), cancellationToken);
        await DbProbe.ExecuteAsync(owner,
            $"""
            CREATE SEQUENCE expiry_save_failures;
            CREATE FUNCTION fail_expiry_save() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS
            'BEGIN PERFORM nextval(''expiry_save_failures''); RAISE EXCEPTION ''forced expiry save failure''; END';
            CREATE TRIGGER expiry_save_fails BEFORE INSERT ON outbox_messages
                FOR EACH ROW WHEN (NEW.payload->>'id' = '{due}') EXECUTE FUNCTION fail_expiry_save();
            """,
            cancellationToken);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid, DatabaseContainers.WorkersLogin);
        settings[ScanKey] = "1";
        await using var factory = new ConfiguredFactory<Program>(settings);

        try
        {
            using var client = factory.CreateClient();
            var worker = (BackgroundService)factory.Services.GetServices<IHostedService>().Single(service => service is ReservationExpiryWorker);

            // Act: wait for at least two failed saves (the second proves a later pass ran), then lift the failure.
            var failures = await PollAsync(
                () => DbProbe.ScalarAsync<long>(owner, "SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM expiry_save_failures", cancellationToken),
                count => count >= 2,
                cancellationToken);
            Assert.True(failures >= 2, $"The forced failure ran {failures} time(s).");
            Assert.NotNull(worker.ExecuteTask);
            Assert.False(worker.ExecuteTask.IsCompleted, $"The worker stopped: {worker.ExecuteTask.Exception}");
            Assert.Equal("Active", await DbProbe.ScalarAsync<string>(app, "SELECT status FROM reservations WHERE id = $1", cancellationToken, due));
            await DbProbe.ExecuteAsync(owner, "DROP TRIGGER expiry_save_fails ON outbox_messages", cancellationToken);

            // Assert
            var status = await PollAsync(
                () => DbProbe.ScalarAsync<string>(app, "SELECT status FROM reservations WHERE id = $1", cancellationToken, due),
                value => value == "Expired",
                cancellationToken);
            Assert.Equal("Expired", status);
        }
        finally
        {
            await DbProbe.ExecuteAsync(owner,
                """
                DROP TRIGGER IF EXISTS expiry_save_fails ON outbox_messages;
                DROP FUNCTION IF EXISTS fail_expiry_save();
                DROP SEQUENCE IF EXISTS expiry_save_failures;
                """,
                CancellationToken.None);
        }
    }

    private static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> done, CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + Patience;
        var value = await read();
        while (!done(value) && TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            value = await read();
        }

        return value;
    }
}
