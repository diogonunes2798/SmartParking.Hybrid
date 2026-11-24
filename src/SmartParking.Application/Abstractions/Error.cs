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

    private Error(string code, string description, ErrorType type, IReadOnlyDictionary<string, string[]> validationErrors)
    {
        Code = code;
        Description = description;
        Type = type;
        ValidationErrors = validationErrors;
    }

    public string Code { get; }

    public string Description { get; }

    public ErrorType Type { get; }

    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    public static Error NotFound(string code, string description) =>
        new(code, description, ErrorType.NotFound, EmptyErrors);

    public static Error Conflict(string code, string description) =>
        new(code, description, ErrorType.Conflict, EmptyErrors);

    public static Error Unauthorized(string code, string description) =>
        new(code, description, ErrorType.Unauthorized, EmptyErrors);

    public static Error Validation(string code, string description, IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new(code, description, ErrorType.Validation, errors);
    }
}
