using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Users;

namespace SmartParking.Api.IntegrationTests.Relational;

/// <summary>
/// D1 default: sessions in PostgreSQL behind <see cref="IParkingSessionRepository"/>.
/// The UC-02 endpoints arrive in Phase 2b; here the repository contract is exercised through DI,
/// exactly as the future handlers will use it.
/// </summary>
[Collection(RelationalTestGroup.Name)]
public sealed class ParkingSessionRepositoryTests(RelationalFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Session_lifecycle_is_persisted_and_the_open_session_of_a_spot_can_be_found()
    {
        var (spotId, vehicleId, userId) = await SeedAsync();
        var startedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.FromHours(1)); // non-UTC on purpose
        var session = ParkingSession.Open(spotId, vehicleId, userId, startedAt);

        await InScopeAsync(async (sessions, uow) =>
        {
            sessions.Add(session);
            await uow.SaveChangesAsync(Ct);
        });

        await InScopeAsync(async (sessions, _) =>
        {
            var open = await sessions.FindOpenBySpotAsync(spotId, Ct);
            Assert.NotNull(open);
            Assert.Equal(session.Id, open.Id);
            Assert.Equal(startedAt, open.StartedAt); // same instant, stored as UTC
            Assert.Equal(TimeSpan.Zero, open.StartedAt.Offset);
        });

        await InScopeAsync(async (sessions, uow) =>
        {
            var loaded = await sessions.GetAsync(session.Id, Ct);
            loaded!.Close(startedAt.AddHours(2), await SeedTariffAsync(), 2.40m);
            await uow.SaveChangesAsync(Ct);
        });

        await InScopeAsync(async (sessions, _) =>
        {
            Assert.Null(await sessions.FindOpenBySpotAsync(spotId, Ct));
            var closed = await sessions.GetAsync(session.Id, Ct);
            Assert.Equal(ParkingSessionStatus.Closed, closed!.Status);
            Assert.Equal(2.40m, closed.Cost);
        });
    }

    /// <summary>The partial unique index enforces «one open session per spot» even if a use case forgets to check.</summary>
    [Fact]
    public async Task Second_open_session_on_the_same_spot_is_rejected_by_the_database()
    {
        var (spotId, vehicleId, userId) = await SeedAsync();
        await InScopeAsync(async (sessions, uow) =>
        {
            sessions.Add(ParkingSession.Open(spotId, vehicleId, userId, DateTimeOffset.UtcNow));
            await uow.SaveChangesAsync(Ct);
        });

        await InScopeAsync(async (sessions, uow) =>
        {
            sessions.Add(ParkingSession.Open(spotId, vehicleId, userId, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<UniqueConstraintException>(() => uow.SaveChangesAsync(Ct));
        });
    }

    /// <summary>Optimistic concurrency (spec §4, UC-02): two writers of the same session, the second one loses.</summary>
    [Fact]
    public async Task Concurrent_modification_of_a_session_raises_a_concurrency_conflict()
    {
        var (spotId, vehicleId, userId) = await SeedAsync();
        var tariffId = await SeedTariffAsync();
        var session = ParkingSession.Open(spotId, vehicleId, userId, DateTimeOffset.UtcNow.AddHours(-1));
        await InScopeAsync(async (sessions, uow) =>
        {
            sessions.Add(session);
            await uow.SaveChangesAsync(Ct);
        });

        await using var scopeA = fixture.Services.CreateAsyncScope();
        await using var scopeB = fixture.Services.CreateAsyncScope();
        var a = await scopeA.ServiceProvider.GetRequiredService<IParkingSessionRepository>().GetAsync(session.Id, Ct);
        var b = await scopeB.ServiceProvider.GetRequiredService<IParkingSessionRepository>().GetAsync(session.Id, Ct);

        a!.Close(DateTimeOffset.UtcNow, tariffId, 1.00m);
        await scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        b!.Close(DateTimeOffset.UtcNow, tariffId, 9.99m);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct));
    }

    private async Task InScopeAsync(Func<IParkingSessionRepository, IUnitOfWork, Task> action)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await action(
            scope.ServiceProvider.GetRequiredService<IParkingSessionRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    private async Task<(Guid SpotId, Guid VehicleId, Guid UserId)> SeedAsync()
    {
        var facility = await fixture.CreateFacilityAsync(ct: Ct);
        var spot = await fixture.CreateSpotAsync(facility.Id, ct: Ct);
        var user = User.Create("Driver", $"driver.{Guid.NewGuid():N}@example.com");
        var vehicle = Vehicle.Create(user.Id, $"T{Guid.NewGuid():N}"[..8], VehicleType.Car);
        await fixture.WithDbAsync(async db =>
        {
            db.Users.Add(user);
            db.Vehicles.Add(vehicle);
            return await db.SaveChangesAsync(Ct);
        });
        return (spot.Id, vehicle.Id, user.Id);
    }

    private Task<Guid> SeedTariffAsync() =>
        fixture.WithDbAsync(async db =>
        {
            var tariff = Domain.Tariffs.Tariff.Create(
                "Centro",
                new Domain.Tariffs.ValidityPeriod(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null),
                new Domain.Tariffs.TariffRules(1.20m, 0, null));
            db.Tariffs.Add(tariff);
            await db.SaveChangesAsync(Ct);
            return tariff.Id;
        });
}
