using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;
using SmartParking.Workers.Reservations;

namespace SmartParking.Workers.Tests.Fixtures;

/// <summary>
/// Starts the three databases once for the Workers host tests. The hosts connect as the Workers login
/// (<see cref="DatabaseContainers.WorkersLogin"/>), never as the owner (spec 002, AC-29).
/// </summary>
public sealed class WorkersFixture : IAsyncLifetime
{
    private ConfiguredFactory<Program>? expiry;

    public DatabaseContainers Databases { get; } = new();

    /// <summary>The clock of the <see cref="Expiry"/> host (spec 002). Each test sets its own instant.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));

    /// <summary>The after-read hook of the <see cref="Expiry"/> host (AC-28: a stale <c>xmin</c> on one reservation).</summary>
    internal AfterReadHook Hook { get; } = new();

    /// <summary>
    /// A Workers host in Hybrid mode with the <see cref="Clock"/> and the <see cref="Hook"/>, and without the expiry
    /// worker: the tests run one <see cref="ReservationExpiryPass"/> themselves (plan 002b, proposal 12).
    /// </summary>
    public ConfiguredFactory<Program> Expiry => expiry ??= CreateFactory(PersistenceMode.Hybrid, services =>
    {
        services.AddSingleton<TimeProvider>(Clock);
        AfterReadReservationRepository.Decorate(services, Hook);
        RemoveExpiryWorker(services);
    });

    public ValueTask InitializeAsync() => Databases.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        if (expiry is not null)
        {
            await expiry.DisposeAsync();
        }

        await Databases.DisposeAsync();
    }

    public ConfiguredFactory<Program> CreateFactory(PersistenceMode mode, Action<IServiceCollection>? configureServices = null) =>
        new(Databases.Settings(mode, DatabaseContainers.WorkersLogin), configureServices: configureServices);

    private static void RemoveExpiryWorker(IServiceCollection services)
    {
        var worker = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(ReservationExpiryWorker));
        services.Remove(worker);
    }
}
