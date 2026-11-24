using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartParking.Application.Abstractions;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Turns constraint violations into <see cref="Error"/> values, so Application stays free of EF Core (spec 001):
/// <c>23505</c>, <c>23503</c> and <c>23P01</c> → Conflict (409); <c>23514</c> → Validation (400).
/// Messages name the constraint and table only: the server's DETAIL (which carries row values) is never copied.
/// </summary>
internal static class PostgresErrorTranslator
{
    public static Error StaleVersion { get; } = Error.PreconditionFailed(
        "Database.StaleVersion",
        "The resource was changed by another request. Read it again and retry with the new ETag.");

    public static bool TryTranslate(DbUpdateException exception, [NotNullWhen(true)] out Error? error)
    {
        ArgumentNullException.ThrowIfNull(exception);

        error = exception.InnerException is PostgresException postgres ? Translate(postgres) : null;
        return error is not null;
    }

    public static Error? Translate(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var constraint = exception.ConstraintName ?? "unknown";

        return exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => Error.Conflict(
                "Database.UniqueViolation",
                $"Another row already has the same unique value ({constraint})."),
            PostgresErrorCodes.ForeignKeyViolation => Error.Conflict(
                "Database.ForeignKeyViolation",
                IsDeleteOfReferencedRow(exception)
                    ? $"It is still referenced by rows in {exception.TableName} ({constraint})."
                    : $"A referenced row does not exist ({constraint})."),
            PostgresErrorCodes.ExclusionViolation => Error.Conflict(
                "Database.ExclusionViolation",
                $"The change overlaps an existing row ({constraint})."),
            PostgresErrorCodes.CheckViolation => Error.Validation(
                "Database.CheckViolation",
                $"The change breaks the rule {constraint}.",
                new Dictionary<string, string[]> { [constraint] = [$"Violates {constraint}."] }),
            _ => null,
        };
    }

    /// <summary>
    /// A RESTRICT hit while deleting the parent, as opposed to a child pointing at a missing parent.
    /// Keys never change, so only a DELETE can break a foreign key from the parent side. The failing command
    /// is used instead of the message text, which depends on the server's <c>lc_messages</c>.
    /// </summary>
    private static bool IsDeleteOfReferencedRow(PostgresException exception) =>
        exception.BatchCommand?.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) == true;
}
