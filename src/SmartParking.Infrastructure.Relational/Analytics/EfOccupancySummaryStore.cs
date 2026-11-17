using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartParking.Application.Analytics;
using SmartParking.Infrastructure.Relational.Retention;

namespace SmartParking.Infrastructure.Relational.Analytics;

/// <summary>
/// Summary store of UC-04 in PostgreSQL (D4 default, ADR-0010). Rows are upserted with
/// <c>INSERT … SELECT unnest(…) ON CONFLICT (facility_id, hour_start) DO UPDATE</c>: one statement per chunk, and saving
/// the same window again replaces the rows with the same values (idempotent). The checkpoint moves only forward
/// (<c>GREATEST</c>), so two Workers instances never move it back.
/// </summary>
internal sealed class EfOccupancySummaryStore(SmartParkingDbContext context) : IOccupancySummaryStore
{
    /// <summary>Name of the worker's checkpoint row.</summary>
    public const string CheckpointName = "occupancy_hourly";

    /// <summary>Rows per statement (8 array parameters; bounded size of one command).</summary>
    private const int ChunkSize = 5_000;

    private const string UpsertSql =
        """
        INSERT INTO analytics.occupancy_hourly
            (facility_id, hour_start, zone, spot_count, occupied_seconds, available_seconds, event_count, aggregated_at)
        SELECT * FROM unnest(@facility_id, @hour_start, @zone, @spot_count, @occupied_seconds, @available_seconds, @event_count, @aggregated_at)
        ON CONFLICT (facility_id, hour_start) DO UPDATE SET
            zone = EXCLUDED.zone,
            spot_count = EXCLUDED.spot_count,
            occupied_seconds = EXCLUDED.occupied_seconds,
            available_seconds = EXCLUDED.available_seconds,
            event_count = EXCLUDED.event_count,
            aggregated_at = EXCLUDED.aggregated_at
        WHERE occupancy_hourly.aggregated_at <= EXCLUDED.aggregated_at
        """;

    public async Task<DateTimeOffset?> GetCheckpointAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await context.Set<AggregationCheckpoint>().AsNoTracking()
            .Where(c => c.Name == CheckpointName)
            .Select(c => (DateTimeOffset?)c.Position)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return checkpoint?.ToUniversalTime();
    }

    public async Task<DateTimeOffset?> SaveAsync(
        IReadOnlyList<OccupancyHourlySummary> rows,
        DateTimeOffset? checkpoint,
        AggregatedRange? covered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var chunk in rows.Chunk(ChunkSize))
            {
                await context.Database.ExecuteSqlRawAsync(
                        UpsertSql,
                        [
                            Array("facility_id", NpgsqlDbType.Uuid, chunk.Select(r => r.FacilityId)),
                            Array("hour_start", NpgsqlDbType.TimestampTz, chunk.Select(r => r.HourStart.UtcDateTime)),
                            Array("zone", NpgsqlDbType.Text, chunk.Select(r => r.Zone)),
                            Array("spot_count", NpgsqlDbType.Integer, chunk.Select(r => r.SpotCount)),
                            Array("occupied_seconds", NpgsqlDbType.Bigint, chunk.Select(r => r.OccupiedSeconds)),
                            Array("available_seconds", NpgsqlDbType.Bigint, chunk.Select(r => r.AvailableSeconds)),
                            Array("event_count", NpgsqlDbType.Integer, chunk.Select(r => r.EventCount)),
                            Array("aggregated_at", NpgsqlDbType.TimestampTz, chunk.Select(r => r.AggregatedAt.UtcDateTime)),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (checkpoint is { } position)
            {
                await context.Database.ExecuteSqlAsync(
                        $"""
                         INSERT INTO analytics.aggregation_checkpoints (name, position) VALUES ({CheckpointName}, {position.ToUniversalTime()})
                         ON CONFLICT (name) DO UPDATE SET position = GREATEST(aggregation_checkpoints.position, EXCLUDED.position)
                         """,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (covered is not null)
            {
                // Retention (Phase 10, ADR-0013). The purge horizon is read FOR SHARE: retention moves it with an upsert of
                // the same row, so it cannot move until this transaction ends; a range that starts before it was read from
                // a log that may have lost events and must not replace good rows (review of Phase 10, finding 4).
                var from = covered.From.ToUniversalTime();
                var to = covered.To.ToUniversalTime();
                var horizon = await context.Database
                    .SqlQuery<DateTimeOffset>(
                        $"SELECT position AS \"Value\" FROM analytics.retention_positions WHERE name = {RetentionPositionRow.PurgeHorizon} FOR SHARE")
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (horizon.Count > 0 && from < horizon[0])
                {
                    throw new InvalidOperationException(
                        $"The aggregated range starts at {from:O}, before the retention purge horizon {horizon[0].ToUniversalTime():O}: not saved.");
                }

                // Coverage: every hour of the range, with or without rows, with the instant the events were read from.
                var aggregatedAt = covered.AggregatedAt.ToUniversalTime();
                await context.Database.ExecuteSqlAsync(
                        $"""
                         INSERT INTO analytics.aggregated_hours (hour_start, aggregated_at)
                         SELECT h, {aggregatedAt} FROM generate_series({from}, {to} - interval '1 hour', interval '1 hour') AS h
                         ON CONFLICT (hour_start) DO UPDATE
                         SET aggregated_at = GREATEST(aggregated_hours.aggregated_at, EXCLUDED.aggregated_at)
                         """,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var stored = await GetCheckpointAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return stored;
        }
    }

    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType type, IEnumerable<T> values) =>
        new(name, NpgsqlDbType.Array | type) { Value = values.ToArray() };
}
