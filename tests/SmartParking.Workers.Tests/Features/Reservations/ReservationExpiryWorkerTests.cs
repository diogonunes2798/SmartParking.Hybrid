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
