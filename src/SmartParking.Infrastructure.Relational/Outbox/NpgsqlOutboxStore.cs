using Npgsql;
using NpgsqlTypes;
using SmartParking.Application.Abstractions;

namespace SmartParking.Infrastructure.Relational.Outbox;

/// <summary>
/// <see cref="IOutboxStore"/> on PostgreSQL (Phase 5, ADR-0009). Plain SQL on the shared data source instead of EF Core:
/// the claim needs <c>FOR UPDATE SKIP LOCKED</c> and the relay needs no change tracking.
/// <list type="bullet">
/// <item>Reads <c>WHERE processed_at IS NULL</c>, never a cursor on <c>occurred_at</c>: a slower transaction can commit a
/// row older than rows already published (review of Phase 2b, finding 2). The partial index
/// <c>ix_outbox_messages_unprocessed</c> serves the query.</item>
/// <item>Processed rows are kept (<c>UPDATE processed_at</c>, not <c>DELETE</c>): they are the record used to measure and
/// audit the relay; their removal is the retention of Phase 10 (RNF-05).</item>
/// </list>
/// </summary>
internal sealed class NpgsqlOutboxStore(NpgsqlDataSource dataSource) : IOutboxStore
{
    /// <summary>
    /// Leading comment of the polling commands (claim and backlog count). They run every 100 ms even when the outbox is
    /// empty, so <see cref="DependencyInjection"/> leaves them out of the Npgsql traces (review of Phase 5, finding 2).
    /// </summary>
    public const string PollingMarker = "/* outbox-relay-poll */";

    private const string ClaimSql = PollingMarker + """
        SELECT id, type, aggregate_id, payload::text, occurred_at, trace_parent
        FROM outbox_messages
        WHERE processed_at IS NULL
        ORDER BY occurred_at, id
        LIMIT $1
        FOR UPDATE SKIP LOCKED
        """;

    private const string CompleteSql = "UPDATE outbox_messages SET processed_at = $1 WHERE id = ANY($2)";

    private const string PendingSql = PollingMarker + " SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL";

    public async Task<IOutboxBatch> ClaimAsync(int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var entries = new List<OutboxEntry>(maxCount);
            var command = new NpgsqlCommand(ClaimSql, connection, transaction);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = maxCount });
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        entries.Add(new OutboxEntry(
                            reader.GetGuid(0),
                            reader.GetString(1),
                            reader.GetGuid(2),
                            reader.GetString(3),
                            reader.GetFieldValue<DateTimeOffset>(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5)));
                    }
                }
            }

            return new Batch(connection, transaction, entries);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<long> CountPendingAsync(CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(PendingSql);
        await using (command.ConfigureAwait(false))
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    /// <summary>Holds the connection and the row locks until disposed; rolls back unless completed.</summary>
    private sealed class Batch(NpgsqlConnection connection, NpgsqlTransaction transaction, List<OutboxEntry> entries) : IOutboxBatch
    {
        public IReadOnlyList<OutboxEntry> Entries => entries;

        public async Task CompleteAsync(DateTimeOffset processedAt, CancellationToken cancellationToken)
        {
            if (entries.Count > 0)
            {
                var command = new NpgsqlCommand(CompleteSql, connection, transaction);
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = processedAt });
                    command.Parameters.Add(new NpgsqlParameter<Guid[]>
                    {
                        TypedValue = [.. entries.Select(e => e.Id)],
                        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                    });
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            // Not completed: rolling back releases the locks and leaves the rows unprocessed (published again later).
            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
