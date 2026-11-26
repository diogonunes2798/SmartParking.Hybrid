using Npgsql;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Recognises a PostgreSQL failure worth retrying on a read (spec 003, AC-13). Npgsql marks those as transient:
/// <list type="bullet">
/// <item>connection and I/O errors;</item>
/// <item>timeouts;</item>
/// <item>server states such as <c>too_many_connections</c> or <c>cannot_connect_now</c>.</item>
/// </list>
/// They can arrive bare, or inside EF Core's «transient failure» <see cref="InvalidOperationException"/>.
/// A non-transient error (a missing grant, a missing table, a failed login) is a configuration problem: it is left to
/// the global handler (500), so that a gateway does not retry it forever (review of F4a, finding 2).
/// </summary>
internal static class PostgresUnavailability
{
    public static bool IsUnavailable(Exception exception) => exception switch
    {
        NpgsqlException { IsTransient: true } or TimeoutException => true,
        InvalidOperationException { InnerException: NpgsqlException { IsTransient: true } or TimeoutException } => true,
        _ => false,
    };
}
