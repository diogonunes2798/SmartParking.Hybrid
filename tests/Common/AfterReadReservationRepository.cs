using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Sessions;

namespace SmartParking.Tests.Common;

/// <summary>
/// Decorates the real reservation repository: right after a chosen reservation is read (tracked, with its
/// <c>xmin</c>), runs an action once, before the caller saves. The action changes the row behind the caller's back,
/// so the save meets a stale <c>xmin</c> for certain (AC-28; plan 002b, proposal 13).
/// </summary>
internal sealed class AfterReadReservationRepository(IReservationRepository inner, AfterReadHook hook) : IReservationRepository
{
    public async Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var reservation = await inner.GetByIdAsync(id, cancellationToken);
        await hook.RunOnceAsync(id, cancellationToken);
        return reservation;
    }

    public Task<bool> IsSpotHeldForAnotherVehicleAsync(
        Guid spotId, Guid vehicleId, DateTimeOffset now, ReservationPolicy policy, CancellationToken cancellationToken) =>
        inner.IsSpotHeldForAnotherVehicleAsync(spotId, vehicleId, now, policy, cancellationToken);

    public void Add(Reservation reservation) => inner.Add(reservation);

    /// <summary>Wraps the registered implementation, so the test needs no access to the internal repository type.</summary>
    public static void Decorate(IServiceCollection services, AfterReadHook hook)
    {
        var registered = services.Last(descriptor => descriptor.ServiceType == typeof(IReservationRepository));
        var implementation = registered.ImplementationType
            ?? throw new InvalidOperationException("IReservationRepository is expected to be registered by type.");

        services.Remove(registered);
        services.AddScoped<IReservationRepository>(provider => new AfterReadReservationRepository(
            (IReservationRepository)ActivatorUtilities.CreateInstance(provider, implementation), hook));
    }
}
