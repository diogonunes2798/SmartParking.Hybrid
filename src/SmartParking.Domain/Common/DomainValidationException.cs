namespace SmartParking.Domain.Common;

/// <summary>
/// Raised when an invariant of a domain entity or value object is violated.
/// The API maps it to <c>400 Bad Request</c>; the domain is the single source of validation rules.
/// </summary>
public sealed class DomainValidationException : Exception
{
    public DomainValidationException(string field, string message)
        : base($"{field}: {message}")
    {
        Field = field;
        Reason = message;
    }

    public DomainValidationException(string field, string message, Exception innerException)
        : base($"{field}: {message}", innerException)
    {
        Field = field;
        Reason = message;
    }

    public DomainValidationException()
        : this("unknown", "invalid value")
    {
    }

    public DomainValidationException(string message)
        : this("unknown", message)
    {
    }

    public DomainValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Field = "unknown";
        Reason = message;
    }

    /// <summary>Name of the offending field (camelCase, as exposed by the API).</summary>
    public string Field { get; }

    /// <summary>Human-readable reason, without the field name.</summary>
    public string Reason { get; }
}
