namespace SmartParking.Application.Abstractions.Messaging;

/// <summary>Handles a command that changes state (writes go to PostgreSQL or to the event log).</summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
