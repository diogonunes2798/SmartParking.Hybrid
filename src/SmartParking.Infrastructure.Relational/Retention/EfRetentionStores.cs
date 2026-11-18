using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Retention;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure.Relational.Retention;

/// <summary>
/// Progress of the retention in PostgreSQL (ADR-0013), used by both variants. Positions move only forward
/// (<c>GREATEST</c>), so two Workers instances never move them back.
/// </summary>
internal sealed class EfRetentionLedger(SmartParkingDbContext context) : IRetentionLedger
{
    public async Task<IReadOnlyDictionary<DateTimeOffset, DateTimeOffset>> GetAggregatedAtPerHourAsync(
        DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var start = since.ToUniversalTime();
        var end = until.ToUniversalTime();
        var rows = await context.Set<AggregatedHourRow>().AsNoTracking()
            .Where(h => h.HourStart >= start && h.HourStart < end)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(h => h.HourStart.ToUniversalTime(), h => h.AggregatedAt.ToUniversalTime());
    }

    public Task<DateTimeOffset?> GetArchivedUntilAsync(CancellationToken cancellationToken) =>
        GetPositionAsync(RetentionPositionRow.ArchivedUntil, cancellationToken);

    public async Task RecordArchiveAsync(ArchivedDay day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(day);
        var end = new DateTimeOffset(day.Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
        var archivedAt = day.ArchivedAt.ToUniversalTime();
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await context.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO analytics.retention_archive (day, file, events, bytes, sha256, archived_at)
                     VALUES ({day.Day}, {day.File}, {day.Events}, {day.Bytes}, {day.Sha256}, {archivedAt})
                     ON CONFLICT (day) DO UPDATE SET file = EXCLUDED.file, events = EXCLUDED.events, bytes = EXCLUDED.bytes,
                         sha256 = EXCLUDED.sha256, archived_at = EXCLUDED.archived_at
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            await AdvanceAsync(RetentionPositionRow.ArchivedUntil, end, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<DateTimeOffset?> GetPurgeHorizonAsync(CancellationToken cancellationToken) =>
        GetPositionAsync(RetentionPositionRow.PurgeHorizon, cancellationToken);

    public async Task<DateTimeOffset> AdvancePurgeHorizonAsync(DateTimeOffset horizon, CancellationToken cancellationToken)
    {
        await AdvanceAsync(RetentionPositionRow.PurgeHorizon, horizon, cancellationToken).ConfigureAwait(false);
        return (await GetPurgeHorizonAsync(cancellationToken).ConfigureAwait(false))!.Value;
    }

    private Task<int> AdvanceAsync(string name, DateTimeOffset position, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO analytics.retention_positions (name, position) VALUES ({name}, {position.ToUniversalTime()})
             ON CONFLICT (name) DO UPDATE SET position = GREATEST(retention_positions.position, EXCLUDED.position)
             """,
            cancellationToken);

    private async Task<DateTimeOffset?> GetPositionAsync(string name, CancellationToken cancellationToken)
    {
        var position = await context.Set<RetentionPositionRow>().AsNoTracking()
            .Where(p => p.Name == name)
            .Select(p => (DateTimeOffset?)p.Position)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return position?.ToUniversalTime();
    }
}

/// <summary>
/// Snapshot of the last event of each spot before the purge horizon (ADR-0013), upserted with <c>unnest</c>. Uses the
/// <see cref="NpgsqlDataSource"/> rather than the DbContext so that it can be a singleton: the snapshot-aware log that reads
/// it wraps a singleton log consumed by singletons (the bus consumers).
/// </summary>
internal sealed class NpgsqlSpotStateSnapshotStore(NpgsqlDataSource dataSource, TimeProvider time) : ISpotStateSnapshotStore
{
    private const string UpsertSql =
        """
        INSERT INTO analytics.spot_state_snapshot AS s (spot_id, facility_id, event_id, type, "timestamp", saved_at)
        SELECT t.spot_id, t.facility_id, t.event_id, t.type, t."timestamp", @saved_at
        FROM unnest(@spot_id, @facility_id, @event_id, @type, @timestamp) AS t(spot_id, facility_id, event_id, type, "timestamp")
        ON CONFLICT (spot_id) DO UPDATE SET facility_id = EXCLUDED.facility_id, event_id = EXCLUDED.event_id, type = EXCLUDED.type,
            "timestamp" = EXCLUDED."timestamp", saved_at = EXCLUDED.saved_at
        WHERE (s."timestamp", s.event_id) < (EXCLUDED."timestamp", EXCLUDED.event_id)
        """;

    private const string SelectSql =
        """SELECT facility_id, event_id, spot_id, type, "timestamp" FROM analytics.spot_state_snapshot""";

    public async Task SaveAsync(IReadOnlyList<SpotSnapshot> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var chunk in entries.Chunk(5_000))
        {
            var command = dataSource.CreateCommand(UpsertSql);
            await using var scope = command.ConfigureAwait(false);
            command.Parameters.Add(Array("spot_id", NpgsqlDbType.Uuid, chunk.Select(e => e.Event.SpotId)));
            command.Parameters.Add(Array("facility_id", NpgsqlDbType.Uuid, chunk.Select(e => e.FacilityId)));
            command.Parameters.Add(Array("event_id", NpgsqlDbType.Uuid, chunk.Select(e => e.Event.EventId)));
            command.Parameters.Add(Array("type", NpgsqlDbType.Smallint, chunk.Select(e => (short)e.Event.Type)));
            command.Parameters.Add(Array("timestamp", NpgsqlDbType.TimestampTz, chunk.Select(e => e.Event.Timestamp.ToUniversalTime())));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("saved_at", NpgsqlDbType.TimestampTz) { TypedValue = time.GetUtcNow().ToUniversalTime() });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<SpotSnapshot>> GetAsync(Guid facilityId, CancellationToken cancellationToken) =>
        ReadAsync(SelectSql + " WHERE facility_id = @facility_id", facilityId, cancellationToken);

    public Task<IReadOnlyList<SpotSnapshot>> GetAllAsync(CancellationToken cancellationToken) =>
        ReadAsync(SelectSql, null, cancellationToken);

    private async Task<IReadOnlyList<SpotSnapshot>> ReadAsync(string sql, Guid? facilityId, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(sql);
        await using var scope = command.ConfigureAwait(false);
        if (facilityId is { } id)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("facility_id", NpgsqlDbType.Uuid) { TypedValue = id });
        }

        var result = new List<SpotSnapshot>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SpotSnapshot(
                reader.GetGuid(0),
                new LatestSpotEvent(reader.GetGuid(1), reader.GetGuid(2), (OccupancyEventType)reader.GetInt16(3), reader.GetFieldValue<DateTimeOffset>(4))));
        }

        return result;
    }

    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType type, IEnumerable<T> values) =>
        new(name, NpgsqlDbType.Array | type) { Value = values.ToArray() };
}
