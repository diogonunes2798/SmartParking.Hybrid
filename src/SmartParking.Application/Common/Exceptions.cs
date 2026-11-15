namespace SmartParking.Application.Common;

/// <summary>The requested resource does not exist (HTTP 404).</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string resource, Guid id)
        : base($"{resource} '{id}' was not found.")
    {
    }

    public NotFoundException()
    {
    }

    public NotFoundException(string message)
        : base(message)
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Base of the conflicts with the current state of the store (HTTP 409).</summary>
public abstract class ConflictException : Exception
{
    protected ConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Optimistic concurrency failure: the resource changed since the client read it.</summary>
public sealed class ConcurrencyConflictException : ConflictException
{
    public ConcurrencyConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A unique key (e.g. spot code within a facility) would be duplicated.</summary>
public sealed class UniqueConstraintException : ConflictException
{
    public UniqueConstraintException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A referenced row is missing, or a row cannot be deleted because others depend on it.</summary>
public sealed class ReferentialIntegrityException : ConflictException
{
    public ReferentialIntegrityException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
