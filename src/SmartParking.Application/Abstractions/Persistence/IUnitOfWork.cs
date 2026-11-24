namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>
/// Unit of work (D10). The DbContext plays this role: it saves the aggregate changes, the audit
/// entries and the outbox messages in one transaction. Repositories never save.
/// Database constraint violations come back as a failed <see cref="Result"/> (spec 001):
/// unique, foreign key and exclusion violations as <see cref="ErrorType.Conflict"/>, check violations
/// as <see cref="ErrorType.Validation"/>, and a stale concurrency token as <see cref="ErrorType.PreconditionFailed"/>.
/// </summary>
public interface IUnitOfWork
{
    Task<Result> SaveChangesAsync(CancellationToken cancellationToken);
}
