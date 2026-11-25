using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Persistence;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>Decorates the real unit of work: waits at the <see cref="ConcurrencyGate"/>, then saves (AC-11, AC-16).</summary>
internal sealed class GatedUnitOfWork(IUnitOfWork inner, ConcurrencyGate gate) : IUnitOfWork
{
    public async Task<Result> SaveChangesAsync(CancellationToken cancellationToken)
    {
        await gate.ArriveAsync(cancellationToken);
        return await inner.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Wraps the registered <see cref="IUnitOfWork"/> factory, so the test needs no access to the internal DbContext.
    /// </summary>
    public static void Decorate(IServiceCollection services, ConcurrencyGate gate)
    {
        var registered = services.Last(descriptor => descriptor.ServiceType == typeof(IUnitOfWork));
        var factory = registered.ImplementationFactory
            ?? throw new InvalidOperationException("IUnitOfWork is expected to be registered with a factory.");

        services.Remove(registered);
        services.AddScoped<IUnitOfWork>(provider => new GatedUnitOfWork((IUnitOfWork)factory(provider), gate));
    }
}
