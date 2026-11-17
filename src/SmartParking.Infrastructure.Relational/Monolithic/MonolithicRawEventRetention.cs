using System.Globalization;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using SmartParking.Application.Retention;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// Retention of the monolithic variant (Phase 10, ADR-0013): the events of a day are exported from
/// <c>monolith.occupancy_events</c>, and a partition is removed with <c>DROP TABLE</c> once its whole range is before the
/// purge horizon. A DROP frees the space at once (no dead tuples, no VACUUM), the relational counterpart of the MongoDB
/// TTL index, but only whole partitions can go: with daily partitions at most one extra day stays.
/// <para>
/// Partitions are recognised by name (<c>occupancy_events_dYYYYMMDD</c>, <c>occupancy_events_yYYYYmMM</c>, created only by
/// the two maintenance functions). The DEFAULT partition is never dropped. <c>DETACH … CONCURRENTLY</c> is not possible
/// with a DEFAULT partition, so the DROP takes ACCESS EXCLUSIVE on the parent briefly, bounded by <c>lock_timeout</c>.
/// </para>
/// </summary>
public sealed class MonolithicRawEventRetention(NpgsqlDataSource dataSource) : IRawEventRetentionStore
{
    private const string PartitionsSql =
        """
        SELECT c.relname
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'monolith.occupancy_events'::regclass
        """;

    public async Task<DateTimeOffset?> GetEarliestTimestampAsync(CancellationToken cancellationToken)
    {
        // Oldest partition with a row, then DEFAULT: a min() over the parent would read every partition.
        DateTimeOffset? earliest = await MinAsync(MonolithicSchema.DefaultPartition, cancellationToken).ConfigureAwait(false);
        foreach (var partition in (await PartitionsAsync(cancellationToken).ConfigureAwait(false)).OrderBy(p => p.From))
        {
            if (await MinAsync(MonolithicSchema.Schema + "." + partition.Name, cancellationToken).ConfigureAwait(false) is { } min)
            {
                return earliest is { } other && other < min ? other : min;
            }
        }

        return earliest;
    }

    public async Task<long> CountAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"""SELECT count(*) FROM {MonolithicSchema.OccupancyEvents} WHERE "timestamp" >= @from AND "timestamp" < @to""");
        await using var scope = command.ConfigureAwait(false);
        AddRange(command, since, until);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async IAsyncEnumerable<string> ExportAsync(
        DateTimeOffset since, DateTimeOffset until, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // One JSON object per row, with the columns as stored (no payload: explicit schema, ADR-0012).
        var command = dataSource.CreateCommand(
            $"""SELECT row_to_json(e)::text FROM {MonolithicSchema.OccupancyEvents} e WHERE "timestamp" >= @from AND "timestamp" < @to""");
        await using var scope = command.ConfigureAwait(false);
        AddRange(command, since, until);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reader.GetString(0);
        }
    }

    public async Task<EnforcementResult> EnforceAsync(DateTimeOffset? horizon, CancellationToken cancellationToken)
    {
        if (horizon is not { } limit)
        {
            return new EnforcementResult(null, []);
        }

        var dropped = new List<string>();
        foreach (var partition in (await PartitionsAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.To <= limit).OrderBy(p => p.From))
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var connectionScope = connection.ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionScope = transaction.ConfigureAwait(false);
            var command = new NpgsqlCommand(
                // The parent is locked first: DROP locks the partition before the parent, the reverse of an insert.
                $"SET LOCAL lock_timeout = '2s'; LOCK TABLE {MonolithicSchema.OccupancyEvents} IN ACCESS EXCLUSIVE MODE; " +
                $"DROP TABLE IF EXISTS {MonolithicSchema.Schema}.{partition.Name}",
                connection,
                transaction);
            await using var commandScope = command.ConfigureAwait(false);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                dropped.Add(partition.Name);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                break; // retried on the next run; never drop out of order
            }
        }

        return new EnforcementResult(null, dropped);
    }

    /// <summary>The time partitions (not DEFAULT) and their ranges, from their names.</summary>
    public async Task<IReadOnlyList<(string Name, DateTimeOffset From, DateTimeOffset To)>> PartitionsAsync(CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(PartitionsSql);
        await using var scope = command.ConfigureAwait(false);
        var result = new List<(string, DateTimeOffset, DateTimeOffset)>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (TryRange(name, out var from, out var to))
            {
                result.Add((name, from, to));
            }
        }

        return result;
    }

    /// <summary>Range of a partition from its name: <c>occupancy_events_dYYYYMMDD</c> (a day) or <c>occupancy_events_yYYYYmMM</c> (a month).</summary>
    public static bool TryRange(string name, out DateTimeOffset from, out DateTimeOffset to)
    {
        const string prefix = "occupancy_events_";
        from = to = default;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name[prefix.Length..];
        if (suffix.Length == 9 && suffix[0] == 'd'
            && DateTime.TryParseExact(suffix[1..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            from = new DateTimeOffset(day, TimeSpan.Zero);
            to = from.AddDays(1);
            return true;
        }

        if (suffix.Length == 8 && suffix[0] == 'y' && suffix[5] == 'm'
            && DateTime.TryParseExact(suffix[1..5] + suffix[6..], "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            from = new DateTimeOffset(month, TimeSpan.Zero);
            to = from.AddMonths(1);
            return true;
        }

        return false;
    }

    private async Task<DateTimeOffset?> MinAsync(string table, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand($"""SELECT min("timestamp") FROM {table}""");
        await using var scope = command.ConfigureAwait(false);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTime utc ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) : null;
    }

    private static void AddRange(NpgsqlCommand command, DateTimeOffset since, DateTimeOffset until)
    {
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("from", NpgsqlDbType.TimestampTz) { TypedValue = since.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("to", NpgsqlDbType.TimestampTz) { TypedValue = until.ToUniversalTime() });
    }
}
