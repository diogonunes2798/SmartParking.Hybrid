using SmartParking.Domain.Sessions;
using SmartParking.Domain.Tariffs;

namespace SmartParking.Application.Abstractions;

/// <summary>
/// Write-side repository of an aggregate (spec §5, Repository pattern). Implemented per engine;
/// changes are persisted by <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
public interface IRepository<T>
    where T : class
{
    /// <summary>Loads the aggregate for modification, or null when it does not exist.</summary>
    Task<T?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);

    void Add(T entity);

    void Remove(T entity);
}

/// <summary>
/// Parking sessions (UC-02). Where they are stored is decision D1 («EM ABERTO»); the default is the
/// relational store. Callers depend only on this interface, but the contract still relies on EF Core change
/// tracking (changes are saved by <see cref="IUnitOfWork"/>, there is no explicit update). Moving sessions to
/// MongoDB (as Cap. 4.4.2 suggests) therefore also needs an explicit save in the contract and an atomicity
/// story with the outbox (ADR-0003, point 5).
/// </summary>
public interface IParkingSessionRepository : IRepository<ParkingSession>
{
    /// <summary>The open session of a spot, if any (a spot has at most one open session).</summary>
    Task<ParkingSession?> FindOpenBySpotAsync(Guid spotId, CancellationToken cancellationToken);
}

/// <summary>Read-only lookup of the tariff that prices a session (UC-02).</summary>
public interface ITariffLookup
{
    /// <summary>
    /// Tariff of the zone of the spot's facility whose validity contains <paramref name="instant"/>; when
    /// several overlap (no rule forbids it yet, diary of Phase 2a), the one that started most recently.
    /// </summary>
    Task<Tariff?> FindInForceForSpotAsync(Guid spotId, DateTimeOffset instant, CancellationToken cancellationToken);
}

/// <summary>
/// Commits the changes of a use case atomically, in one transaction, together with the outbox rows of the
/// domain events they raised (ADR-0004). Store-specific failures are translated into the
/// exceptions of <see cref="Common"/> so handlers and endpoints stay persistence-ignorant.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="Common.ConcurrencyConflictException">The row changed since it was loaded.</exception>
    /// <exception cref="Common.UniqueConstraintException">A unique key would be duplicated.</exception>
    /// <exception cref="Common.ReferentialIntegrityException">A referenced row is missing or a dependent row exists.</exception>
    /// <exception cref="Domain.Common.DomainValidationException">The store rejected a value (CHECK constraint, invalid data).</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Who performs the current operation, recorded in the audit log (RNF-06).</summary>
public interface ICurrentActor
{
    string Name { get; }
}
