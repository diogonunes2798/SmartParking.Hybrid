using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Counts every SQL command EF Core sends (spec 003, AC-11): readers, non-queries and scalars, sync and async.
/// Reset it before the step being measured.
/// </summary>
internal sealed class CountingCommandInterceptor : DbCommandInterceptor
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public void Reset() => Interlocked.Exchange(ref count, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref count);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Interlocked.Increment(ref count);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Interlocked.Increment(ref count);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
