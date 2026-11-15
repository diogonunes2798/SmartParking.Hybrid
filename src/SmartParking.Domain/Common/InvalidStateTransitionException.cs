namespace SmartParking.Domain.Common;

/// <summary>
/// The operation is not allowed in the current state of the entity (e.g. paying a session that is still
/// open, or closing it twice). It is a conflict with the stored state, not invalid input: the API maps it to
/// <c>409 Conflict</c>, the same status as an optimistic-concurrency conflict, so two concurrent closes
/// give 409 whether the loser fails on the concurrency token or reads the already-closed session.
/// </summary>
public sealed class InvalidStateTransitionException : Exception
{
    public InvalidStateTransitionException(string message)
        : base(message)
    {
    }

    public InvalidStateTransitionException()
    {
    }

    public InvalidStateTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
