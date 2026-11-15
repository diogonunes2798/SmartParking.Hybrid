using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Common;
using SmartParking.Domain.Common;
using SmartParking.Domain.Sessions;

namespace SmartParking.Infrastructure.Relational.Persistence;

/// <summary>
/// Unit of work over <see cref="SmartParkingDbContext"/>. Translates PostgreSQL failures into the
/// persistence-ignorant exceptions of the Application and Domain layers (mapped to HTTP 409 or 400 by the API).
/// </summary>
internal sealed class EfUnitOfWork(SmartParkingDbContext context) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The resource was modified by another request.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw pg.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation =>
                    new UniqueConstraintException($"Duplicate value (constraint '{pg.ConstraintName}').", ex),
                PostgresErrorCodes.ForeignKeyViolation =>
                    new ReferentialIntegrityException(
                        $"The operation violates a reference between resources (constraint '{pg.ConstraintName}').", ex),

                // Safety net: the domain should reject these first. If a CHECK constraint or a data exception
                // (class 22: invalid text, numeric overflow, ...) still fires, it is invalid input (400), not a 500.
                PostgresErrorCodes.CheckViolation =>
                    new DomainValidationException(pg.ConstraintName ?? "unknown", "violates a database constraint", ex),
                _ when pg.SqlState.StartsWith("22", StringComparison.Ordinal) =>
                    new DomainValidationException("unknown", $"invalid value ({pg.SqlState})", ex),
                _ => ex,
            };
        }
    }
}

/// <summary>Generic EF Core repository; aggregates are loaded tracked, for modification.</summary>
internal class EfRepository<T>(SmartParkingDbContext context) : IRepository<T>
    where T : class
{
    protected SmartParkingDbContext Context { get; } = context;

    public async Task<T?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await Context.Set<T>().FindAsync([id], cancellationToken).ConfigureAwait(false);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        Context.Set<T>().AnyAsync(e => EF.Property<Guid>(e, "Id") == id, cancellationToken);

    public void Add(T entity) => Context.Set<T>().Add(entity);

    public void Remove(T entity) => Context.Set<T>().Remove(entity);
}

/// <summary>Relational implementation of D1 (default value): sessions in PostgreSQL.</summary>
internal sealed class EfParkingSessionRepository(SmartParkingDbContext context)
    : EfRepository<ParkingSession>(context), IParkingSessionRepository
{
    public Task<ParkingSession?> FindOpenBySpotAsync(Guid spotId, CancellationToken cancellationToken) =>
        Context.Sessions.SingleOrDefaultAsync(
            s => s.SpotId == spotId && s.Status == ParkingSessionStatus.Open, cancellationToken);
}
