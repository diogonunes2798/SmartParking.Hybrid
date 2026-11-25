using Npgsql;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Recognises a PostgreSQL failure on a read (spec 003, AC-13). The ways it can arrive:
/// <list type="bullet">
/// <item>an <see cref="NpgsqlException"/>, including a <see cref="PostgresException"/> from the server;</item>
/// <item>EF Core's «transient failure» <see cref="InvalidOperationException"/> with the <see cref="NpgsqlException"/> inside;</item>
/// <item>a <see cref="TimeoutException"/>.</item>
/// </list>
/// Anything else is a programming error and is left to the global handler (500).
/// </summary>
internal static class PostgresUnavailability
{
    public static bool IsUnavailable(Exception exception) => exception switch
    {
        NpgsqlException or TimeoutException => true,
        InvalidOperationException { InnerException: NpgsqlException or TimeoutException } => true,
        _ => false,
    };
}
