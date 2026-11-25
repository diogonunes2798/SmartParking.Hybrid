namespace SmartParking.Application.Abstractions;

/// <summary>
/// A typed, expected failure. Validation errors carry the per-field messages
/// that become the <c>errors</c> dictionary of a ValidationProblemDetails.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Name fixed by spec 000 (Result/Error pattern); the assembly is consumed from C# only.")]
public sealed record Error
{
    private static readonly IReadOnlyDictionary<string, string[]> EmptyErrors =
        new Dictionary<string, string[]>();

    private static readonly IReadOnlyDictionary<string, object?> EmptyExtensions =
        new Dictionary<string, object?>();

    private Error(
        string code,
        string description,
        ErrorType type,
        IReadOnlyDictionary<string, string[]> validationErrors,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        Code = code;
        Description = description;
        Type = type;
        ValidationErrors = validationErrors;
        Extensions = extensions ?? EmptyExtensions;
    }

    public string Code { get; }

    public string Description { get; }

    public ErrorType Type { get; }

    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>Extra members of the ProblemDetails (for example the <c>paymentId</c> of a declined payment).</summary>
    public IReadOnlyDictionary<string, object?> Extensions { get; }

    public static Error NotFound(string code, string description) =>
        new(code, description, ErrorType.NotFound, EmptyErrors);

    public static Error Conflict(string code, string description) =>
        new(code, description, ErrorType.Conflict, EmptyErrors);

    public static Error Unauthorized(string code, string description) =>
        new(code, description, ErrorType.Unauthorized, EmptyErrors);

    public static Error PreconditionFailed(string code, string description) =>
        new(code, description, ErrorType.PreconditionFailed, EmptyErrors);

    public static Error PreconditionRequired(string code, string description) =>
        new(code, description, ErrorType.PreconditionRequired, EmptyErrors);

    /// <summary>402: the gateway declined the charge; <paramref name="paymentId"/> is the recorded failed payment (spec 002).</summary>
    public static Error PaymentDeclined(string code, string description, Guid paymentId) =>
        new(code, description, ErrorType.PaymentDeclined, EmptyErrors, new Dictionary<string, object?> { ["paymentId"] = paymentId });

    public static Error Unavailable(string code, string description) =>
        new(code, description, ErrorType.Unavailable, EmptyErrors);

    public static Error Validation(string code, string description, IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new(code, description, ErrorType.Validation, errors);
    }
}
