namespace SmartParking.Application.Abstractions.Messaging;

/// <summary>Handles a read-only query through a dedicated read port (no repositories on the read side, D10).</summary>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
