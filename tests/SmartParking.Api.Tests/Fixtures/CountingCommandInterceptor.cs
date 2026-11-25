using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>Counts the SQL commands that EF Core sends to read data (spec 003, AC-11). Reset it before the step being measured.</summary>
internal sealed class CountingCommandInterceptor : DbCommandInterceptor
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public void Reset() => Interlocked.Exchange(ref count, 0);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
