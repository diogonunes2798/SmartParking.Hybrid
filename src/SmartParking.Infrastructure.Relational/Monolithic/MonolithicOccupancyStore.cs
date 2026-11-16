using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// Occupancy events of the monolithic variant (ADR-0012): the partitioned table <c>monolith.occupancy_events</c>, with
/// the current state of each spot (<c>monolith.spot_state</c>) updated in the same transaction. It plays the role of
/// MongoDB (the log, D3) and of the Redis state of the spots (UC-03) at once, as a single relational system would.
/// <list type="bullet">
/// <item>Idempotent by <c>(event_id, "timestamp")</c>: PostgreSQL requires every unique key of a partitioned table to
/// contain the partition key. A repeated event (bus redelivery, repeated request) carries the same timestamp, so in
/// practice this is idempotency by <c>event_id</c>; an <c>event_id</c> reused with another timestamp would be stored twice.</item>
/// <item>Explicit schema (UC-05 decision of Phase 8): there is no <c>payload</c> column. The generation-2 readings are
/// stored only once the <c>AddEnvironmentalColumns</c> migration has added their columns.</item>
/// <item><c>spot_state</c> keeps the newest event of each spot by <c>("timestamp", event_id)</c>, like the Redis read model:
/// a late event never overwrites a newer one.</item>
/// </list>
/// </summary>
public sealed class MonolithicOccupancyStore(
    NpgsqlDataSource dataSource,
    EnvironmentalColumnsProbe environmentalColumns,
    TimeProvider time) : IOccupancyEventLog
{
    /// <summary>Payload fields of the UC-05 generation-2 sensors, in the order of <see cref="MonolithicSchema.EnvironmentalColumns"/>.</summary>
    public static IReadOnlyList<string> EnvironmentalFields { get; } = ["temperature", "airQuality", "noise"];

    public Task<AppendResult> AppendAsync(IReadOnlyList<OccupancyEventRecord> records, CancellationToken cancellationToken) =>
        AppendCoreAsync(records, cancellationToken);

    /// <summary>
    /// Appends the records and moves the state of their spots, in one transaction. Besides the <see cref="AppendResult"/>,
    /// fills <paramref name="inserted"/> (new events) and <paramref name="applied"/> (new events that became the state of a
    /// registered spot) for the metrics of the bus. As in the Redis projector, an event of an unknown spot or older than
    /// the stored state does not change the state (review of Phase 8, architecture findings M1 and L2). One difference:
    /// when one batch has several events of the same spot, only the newest counts as applied.
    /// </summary>
    internal async Task<AppendResult> AppendCoreAsync(
        IReadOnlyList<OccupancyEventRecord> records,
        CancellationToken cancellationToken,
        List<OccupancyEventRecord>? inserted = null,
        List<OccupancyEventRecord>? applied = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return AppendResult.Empty;
        }

        var withColumns = await environmentalColumns.ExistAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<OccupancyEventRecord>(records.Count);
        var readings = new List<double?[]>(records.Count);
        var refused = new List<Guid>();
        var seen = new HashSet<Guid>();
        var repeated = new List<Guid>();
        foreach (var record in records)
        {
            if (!TryReadPayload(record.PayloadJson, withColumns, out var values))
            {
                refused.Add(record.EventId); // not a JSON object: refused, as by the MongoDB log
            }
            else if (!seen.Add(record.EventId))
            {
                repeated.Add(record.EventId); // the same event twice in one call: one row, one duplicate
            }
            else
            {
                rows.Add(record);
                readings.Add(values);
            }
        }

        if (rows.Count == 0)
        {
            return new AppendResult(0, repeated, refused);
        }

        var storedAt = time.GetUtcNow().ToUniversalTime();
        var command = dataSource.CreateCommand(withColumns ? InsertWithEnvironmentSql : InsertSql);
        await using var commandScope = command.ConfigureAwait(false);
        var p = command.Parameters;
        p.Add(Array("event_ids", NpgsqlDbType.Uuid, rows.Select(r => r.EventId).ToArray()));
        p.Add(Array("sensor_ids", NpgsqlDbType.Uuid, rows.Select(r => r.SensorId).ToArray()));
        p.Add(Array("spot_ids", NpgsqlDbType.Uuid, rows.Select(r => r.SpotId).ToArray()));
        p.Add(Array("facility_ids", NpgsqlDbType.Uuid, rows.Select(r => r.FacilityId).ToArray()));
        p.Add(Array("types", NpgsqlDbType.Smallint, rows.Select(r => (short)r.Type).ToArray()));
        p.Add(Array("timestamps", NpgsqlDbType.TimestampTz, rows.Select(r => r.Timestamp.ToUniversalTime()).ToArray()));
        p.Add(Array("received_ats", NpgsqlDbType.TimestampTz, rows.Select(r => r.ReceivedAt.ToUniversalTime()).ToArray()));
        p.Add(new NpgsqlParameter<DateTimeOffset>("stored_at", NpgsqlDbType.TimestampTz) { TypedValue = storedAt });
        if (withColumns)
        {
            for (var c = 0; c < MonolithicSchema.EnvironmentalColumns.Count; c++)
            {
                p.Add(Array(MonolithicSchema.EnvironmentalColumns[c] + "s", NpgsqlDbType.Double, readings.Select(v => v[c]).ToArray()));
            }
        }

        // A single statement is a single transaction (autocommit): the events and the spot states commit together.
        var insertedIds = new HashSet<Guid>();
        var appliedIds = new HashSet<Guid>();
        NpgsqlDataReader insertReader;
        try
        {
            insertReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (withColumns && ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            // The UC-05 columns were removed (AddEnvironmentalColumns rolled back) while the API ran: check again and write
            // without them (review of Phase 8, migrations finding 7). The retry sees withColumns = false, so it cannot loop.
            environmentalColumns.Reset();
            return await AppendCoreAsync(records, cancellationToken, inserted, applied).ConfigureAwait(false);
        }

        await using (insertReader.ConfigureAwait(false))
        {
            while (await insertReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                insertedIds.Add(insertReader.GetGuid(0));
                if (insertReader.GetBoolean(1))
                {
                    appliedIds.Add(insertReader.GetGuid(0));
                }
            }
        }

        var duplicates = rows.Where(r => !insertedIds.Contains(r.EventId)).Select(r => r.EventId).Concat(repeated).ToList();
        inserted?.AddRange(rows.Where(r => insertedIds.Contains(r.EventId)));
        applied?.AddRange(rows.Where(r => appliedIds.Contains(r.EventId)));
        return new AppendResult(insertedIds.Count, duplicates, refused);
    }

    /// <summary>Last event of every spot (read model rebuild, ADR-0008). Not used by the monolithic variant itself.</summary>
    public async IAsyncEnumerable<LatestSpotEvent> GetLatestPerSpotAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"""
            SELECT DISTINCT ON (spot_id) event_id, spot_id, type, "timestamp"
            FROM {MonolithicSchema.OccupancyEvents}
            ORDER BY spot_id, "timestamp" DESC, event_id DESC
            """);
        await using var commandScope = command.ConfigureAwait(false);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadLatest(reader);
        }
    }

    /// <summary>
    /// State of each spot of one facility just before <paramref name="before"/> (UC-04, ADR-0010), the counterpart of the
    /// MongoDB DISTINCT_SCAN: about one index descent per spot instead of every event of the facility's history.
    /// </summary>
    public async Task<IReadOnlyList<LatestSpotEvent>> GetLatestPerSpotAsync(
        Guid facilityId, DateTimeOffset before, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(LatestPerSpotSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new NpgsqlParameter<Guid>("facility_id", NpgsqlDbType.Uuid) { TypedValue = facilityId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("before", NpgsqlDbType.TimestampTz) { TypedValue = before.ToUniversalTime() });
        var result = new List<LatestSpotEvent>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadLatest(reader));
        }

        return result;
    }

    /// <summary>
    /// The query of <see cref="GetLatestPerSpotAsync(Guid, DateTimeOffset, CancellationToken)"/> (public for the plan test).
    /// PostgreSQL 16 has no skip scan: a plain <c>DISTINCT ON (spot_id)</c> reads every event of the facility since the start of
    /// the history (review of Phase 8, migrations finding 1). The recursive CTE emulates the skip scan on the
    /// <c>(facility_id, spot_id, "timestamp" DESC, event_id DESC)</c> index: each step jumps to the next spot id, and the
    /// lateral probe reads the newest event of that spot (one descent per partition). It keeps the semantics of the MongoDB
    /// pipeline: every spot present in the log, registered or not.
    /// </summary>
    public const string LatestPerSpotSql =
        $"""
        WITH RECURSIVE spots AS (
            (SELECT spot_id FROM {MonolithicSchema.OccupancyEvents}
             WHERE facility_id = @facility_id AND "timestamp" < @before
             ORDER BY spot_id LIMIT 1)
            UNION ALL
            SELECT (SELECT e.spot_id FROM {MonolithicSchema.OccupancyEvents} e
                    WHERE e.facility_id = @facility_id AND e."timestamp" < @before AND e.spot_id > s.spot_id
                    ORDER BY e.spot_id LIMIT 1)
            FROM spots s
            WHERE s.spot_id IS NOT NULL
        )
        SELECT l.event_id, l.spot_id, l.type, l."timestamp"
        FROM spots s
        CROSS JOIN LATERAL (
            SELECT e.event_id, e.spot_id, e.type, e."timestamp"
            FROM {MonolithicSchema.OccupancyEvents} e
            WHERE e.facility_id = @facility_id AND e.spot_id = s.spot_id AND e."timestamp" < @before
            ORDER BY e."timestamp" DESC, e.event_id DESC
            LIMIT 1
        ) l
        WHERE s.spot_id IS NOT NULL
        """;

    /// <summary>Events of one facility in [since, until): a range scan of <c>(facility_id, "timestamp")</c>, pruned to the partitions of the range.</summary>
    public async Task<IReadOnlyList<SpotOccupancyEvent>> GetEventsAsync(
        Guid facilityId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(EventsSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new NpgsqlParameter<Guid>("facility_id", NpgsqlDbType.Uuid) { TypedValue = facilityId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("since", NpgsqlDbType.TimestampTz) { TypedValue = since.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("until", NpgsqlDbType.TimestampTz) { TypedValue = until.ToUniversalTime() });
        var result = new List<SpotOccupancyEvent>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SpotOccupancyEvent(
                reader.GetGuid(0), reader.GetGuid(1), (OccupancyEventType)reader.GetInt16(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return result;
    }

    /// <summary>The query of <see cref="GetEventsAsync"/> (public for the partition pruning test).</summary>
    public const string EventsSql =
        $"""
        SELECT event_id, spot_id, type, "timestamp"
        FROM {MonolithicSchema.OccupancyEvents}
        WHERE facility_id = @facility_id AND "timestamp" >= @since AND "timestamp" < @until
        """;

    private static LatestSpotEvent ReadLatest(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), (OccupancyEventType)reader.GetInt16(2), reader.GetFieldValue<DateTimeOffset>(3));

    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType type, T[] values) =>
        new(name, NpgsqlDbType.Array | type) { Value = values };

    /// <summary>
    /// The payload must be a JSON object (as for the MongoDB log). When the UC-05 columns exist, numeric readings are
    /// taken from it; anything else (missing, text, nested) is stored as NULL: "sensor without that reading" (ADR-0011).
    /// </summary>
    public static bool TryReadPayload(string json, bool withColumns, out double?[] values)
    {
        values = new double?[EnvironmentalFields.Count];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (withColumns)
            {
                for (var i = 0; i < EnvironmentalFields.Count; i++)
                {
                    if (document.RootElement.TryGetProperty(EnvironmentalFields[i], out var value)
                        && value.ValueKind == JsonValueKind.Number
                        && value.TryGetDouble(out var number)
                        && double.IsFinite(number))
                    {
                        values[i] = number;
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // One statement: insert the new events (duplicates skipped by the unique key), then move each spot to its newest
    // inserted event if it is newer than the stored state. Rows are fed in event_id order and the spot states in spot_id
    // order, so overlapping batches usually take their locks in the same order. SQL does not guarantee that an
    // INSERT … SELECT … ORDER BY inserts in that order, so a deadlock remains possible; PostgresEventBus retries it
    // (review of Phase 8, migrations finding 6).
    private const string BaseColumns = """event_id, sensor_id, spot_id, facility_id, type, "timestamp", received_at, stored_at""";

    private const string EnvironmentColumns = BaseColumns + ", temperature, air_quality, noise";

    private static string InsertCte(string columns) =>
        $"""
        ), inserted AS (
            INSERT INTO {MonolithicSchema.OccupancyEvents} AS e ({columns})
            SELECT {columns} FROM input
            ORDER BY event_id
            ON CONFLICT DO NOTHING
            RETURNING e.event_id, e.spot_id, e.type, e."timestamp"
        ), latest AS (
            SELECT DISTINCT ON (spot_id) spot_id, type, event_id, "timestamp"
            FROM inserted
            ORDER BY spot_id, "timestamp" DESC, event_id DESC
        ), state AS (
            INSERT INTO {MonolithicSchema.SpotState} AS s (spot_id, type, event_id, "timestamp", updated_at)
            SELECT l.spot_id, l.type, l.event_id, l."timestamp", @stored_at
            FROM latest l
            JOIN parking_spots p ON p.id = l.spot_id
            ORDER BY l.spot_id
            ON CONFLICT (spot_id) DO UPDATE
            SET type = excluded.type, event_id = excluded.event_id, "timestamp" = excluded."timestamp", updated_at = excluded.updated_at
            WHERE (s."timestamp", s.event_id) < (excluded."timestamp", excluded.event_id)
            RETURNING s.event_id
        )
        SELECT i.event_id, st.event_id IS NOT NULL AS applied
        FROM inserted i
        LEFT JOIN state st ON st.event_id = i.event_id
        """;

    private static readonly string InsertSql =
        """
        WITH input AS (
            SELECT t.event_id, t.sensor_id, t.spot_id, t.facility_id, t.type, t."timestamp", t.received_at, @stored_at AS stored_at
            FROM unnest(@event_ids, @sensor_ids, @spot_ids, @facility_ids, @types, @timestamps, @received_ats)
                AS t(event_id, sensor_id, spot_id, facility_id, type, "timestamp", received_at)
        """ + InsertCte(BaseColumns);

    private static readonly string InsertWithEnvironmentSql =
        """
        WITH input AS (
            SELECT t.event_id, t.sensor_id, t.spot_id, t.facility_id, t.type, t."timestamp", t.received_at, @stored_at AS stored_at,
                   t.temperature, t.air_quality, t.noise
            FROM unnest(@event_ids, @sensor_ids, @spot_ids, @facility_ids, @types, @timestamps, @received_ats,
                        @temperatures, @air_qualitys, @noises)
                AS t(event_id, sensor_id, spot_id, facility_id, type, "timestamp", received_at, temperature, air_quality, noise)
        """ + InsertCte(EnvironmentColumns);
}
