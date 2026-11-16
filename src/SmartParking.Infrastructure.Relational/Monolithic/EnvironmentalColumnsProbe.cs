using Npgsql;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// Whether the UC-05 columns exist in <c>monolith.occupancy_events</c> (ADR-0012). The answer is cached for
/// <see cref="MonolithicOptions.SchemaRefreshInterval"/>, so that the writer starts filling the columns a few seconds after
/// the <c>AddEnvironmentalColumns</c> migration, without a restart: the same API runs before and after the migration, as
/// in the hybrid run of the <c>schema-evolution</c> scenario. Once the columns exist, they are not checked again.
/// </summary>
public sealed class EnvironmentalColumnsProbe(NpgsqlDataSource dataSource, MonolithicOptions options, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool exist;
    private DateTimeOffset checkedAt = DateTimeOffset.MinValue;

    public async ValueTask<bool> ExistAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref exist))
        {
            return true;
        }

        if (time.GetUtcNow() - checkedAt < options.SchemaRefreshInterval)
        {
            return false;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!exist && time.GetUtcNow() - checkedAt >= options.SchemaRefreshInterval)
            {
                var command = dataSource.CreateCommand(
                    $"""
                    SELECT count(*) FROM pg_attribute
                    WHERE attrelid = '{MonolithicSchema.OccupancyEvents}'::regclass
                      AND attname = ANY(@names) AND NOT attisdropped
                    """);
                await using var commandScope = command.ConfigureAwait(false);
                command.Parameters.AddWithValue("names", MonolithicSchema.EnvironmentalColumns.ToArray());
                var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                Volatile.Write(ref exist, count == MonolithicSchema.EnvironmentalColumns.Count);
                checkedAt = time.GetUtcNow();
            }

            return exist;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forgets the cached answer (tests that migrate up and down).</summary>
    public void Reset()
    {
        Volatile.Write(ref exist, false);
        checkedAt = DateTimeOffset.MinValue;
    }

    public void Dispose() => gate.Dispose();
}
