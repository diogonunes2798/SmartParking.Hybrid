namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>
/// Unit of work (D10). The DbContext plays this role: it saves the aggregate changes and the
/// outbox messages in one transaction. Repositories never save. Implemented in F2.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
