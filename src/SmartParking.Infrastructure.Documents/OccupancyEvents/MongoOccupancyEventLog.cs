using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure.Documents.OccupancyEvents;

/// <summary>
/// Immutable occupancy event log in MongoDB (D3 default, ADR-0007). Only <c>insertMany</c> is ever issued: no
/// update, no replace, no delete (retention is Phase 10). Document shape:
/// <code>
/// { _id: ObjectId, eventId: UUID, sensorId: UUID, spotId: UUID, facilityId: UUID, type: "OCCUPIED" | "FREED",
///   timestamp: Date, payload: { …as received… }, receivedAt: Date, storedAt: Date }
/// </code>
/// <list type="bullet">
/// <item>GUIDs are BSON binary subtype 4 (standard UUID), readable as <c>UUID("…")</c> in mongosh;</item>
/// <item><c>timestamp</c> is a BSON Date, never a string: a string would sort lexically and break the
/// <c>(facilityId, timestamp)</c> index for range queries. BSON Dates have millisecond precision;</item>
/// <item><c>payload</c> is the sensor's JSON object parsed as is (UC-05, RNF-04): no schema, no mapping.</item>
/// </list>
/// Idempotency: a unique index on <c>eventId</c>; duplicates are counted, not failed (at-least-once bus).
/// </summary>
public sealed class MongoOccupancyEventLog(IMongoDatabase database, TimeProvider time) : IOccupancyEventLog, IDisposable
{
    public const string CollectionName = "occupancy_events";
    public const string EventIdIndexName = "ux_eventId";
    public const string FacilityTimestampIndexName = "ix_facilityId_timestamp";
    public const string FacilitySpotTimestampIndexName = "ix_facilityId_spotId_timestamp_eventId";

    private const int DuplicateKeyCode = 11000;

    private readonly IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(CollectionName);
    private readonly SemaphoreSlim indexLock = new(1, 1);
    private volatile bool indexesReady;

    public async Task<AppendResult> AppendAsync(IReadOnlyList<OccupancyEventRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return AppendResult.Empty;
        }

        // The unique index must exist before the first insert, otherwise a duplicate could slip in.
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

        var storedAt = time.GetUtcNow();
        var documents = new List<BsonDocument>(records.Count);
        var refused = new List<Guid>();
        foreach (var record in records)
        {
            if (ToDocument(record, storedAt) is { } document)
            {
                documents.Add(document);
            }
            else
            {
                refused.Add(record.EventId);
            }
        }

        if (documents.Count == 0)
        {
            return new AppendResult(0, [], refused);
        }

        try
        {
            // Unordered: one duplicate does not stop the other inserts of the batch.
            await collection.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }, cancellationToken)
                .ConfigureAwait(false);
            return new AppendResult(documents.Count, [], refused);
        }
        catch (MongoBulkWriteException<BsonDocument> ex)
            when (ex.WriteConcernError is null && !ex.WriteErrors.Any(e => e.Category == ServerErrorCategory.ExecutionTimeout))
        {
            // Document-level write errors are deterministic for that document, except ExecutionTimeout (review of
            // Phase 3, finding 3): with one of those the exception propagates and the whole batch is retried, which
            // the unique index makes safe. Transient failures (network, primary step-down, write concern) surface
            // as other exceptions and are rethrown for a retry as well.
            var duplicates = ex.WriteErrors
                .Where(e => e.Category == ServerErrorCategory.DuplicateKey || e.Code == DuplicateKeyCode)
                .Select(e => EventId(documents[e.Index]))
                .ToList();
            refused.AddRange(ex.WriteErrors
                .Where(e => e.Category != ServerErrorCategory.DuplicateKey && e.Code != DuplicateKeyCode)
                .Select(e => EventId(documents[e.Index])));

            return new AppendResult(documents.Count - ex.WriteErrors.Count, duplicates, refused);
        }
    }

    /// <summary>
    /// Last event of every spot, for the read model rebuild (ADR-0008). One <c>$group</c> with <c>$top</c> (MongoDB
    /// 5.2+), which keeps one document per spot while it scans, instead of sorting the whole collection first. It is a
    /// collection scan: no index on <c>spotId</c> is added for an operation that runs only on rebuilds, because every
    /// index costs on each of the ~1.4 million inserts per day (D8). <c>allowDiskUse</c> for very large logs.
    /// </summary>
    public async IAsyncEnumerable<LatestSpotEvent> GetLatestPerSpotAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BsonDocument[] pipeline =
        [
            new("$group", new BsonDocument
            {
                { "_id", "$spotId" },
                {
                    "last", new BsonDocument("$top", new BsonDocument
                    {
                        { "sortBy", new BsonDocument { { "timestamp", -1 }, { "eventId", -1 } } },
                        { "output", new BsonDocument { { "eventId", "$eventId" }, { "type", "$type" }, { "timestamp", "$timestamp" } } },
                    })
                },
            }),
        ];

        using var cursor = await collection
            .AggregateAsync<BsonDocument>(pipeline, new AggregateOptions { AllowDiskUse = true }, cancellationToken)
            .ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                var last = document["last"].AsBsonDocument;
                yield return new LatestSpotEvent(
                    last["eventId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                    document["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                    last["type"].AsString == "OCCUPIED" ? OccupancyEventType.Occupied : OccupancyEventType.Freed,
                    new DateTimeOffset(last["timestamp"].ToUniversalTime(), TimeSpan.Zero));
            }
        }
    }

    /// <summary>
    /// State of each spot of one facility just before <paramref name="before"/>, with no lower bound (UC-04 aggregation,
    /// ADR-0010, review of Phase 6, findings 1 and 2): the last event of each spot, however old. <c>$sort</c> on the
    /// <c>(facilityId, spotId, timestamp desc, eventId desc)</c> index followed by <c>$group</c> with <c>$first</c> is
    /// answered with a <c>DISTINCT_SCAN</c>: about one index entry per spot instead of every event of the period.
    /// </summary>
    public async Task<IReadOnlyList<LatestSpotEvent>> GetLatestPerSpotAsync(
        Guid facilityId, DateTimeOffset before, CancellationToken cancellationToken)
    {
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<LatestSpotEvent>();
        using var cursor = await collection
            .AggregateAsync<BsonDocument>(LatestPerSpotPipeline(facilityId, before), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                result.Add(new LatestSpotEvent(
                    document["eventId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                    document["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                    Type(document["type"]),
                    Timestamp(document["timestamp"])));
            }
        }

        return result;
    }

    /// <summary>The pipeline of <see cref="GetLatestPerSpotAsync(Guid, DateTimeOffset, CancellationToken)"/> (public for the explain test).</summary>
    public static BsonDocument[] LatestPerSpotPipeline(Guid facilityId, DateTimeOffset before) =>
    [
        new("$match", new BsonDocument
        {
            { "facilityId", Uuid(facilityId) },
            { "timestamp", new BsonDocument("$lt", new BsonDateTime(before.UtcDateTime)) },
        }),
        new("$sort", new BsonDocument { { "facilityId", 1 }, { "spotId", 1 }, { "timestamp", -1 }, { "eventId", -1 } }),
        new("$group", new BsonDocument
        {
            { "_id", "$spotId" },
            { "eventId", new BsonDocument("$first", "$eventId") },
            { "type", new BsonDocument("$first", "$type") },
            { "timestamp", new BsonDocument("$first", "$timestamp") },
        }),
    ];

    /// <summary>
    /// Events of one facility in [<paramref name="since"/>, <paramref name="until"/>) (UC-04 aggregation, ADR-0010). A range
    /// scan of the <c>(facilityId, timestamp)</c> index; only the four fields the aggregation needs are returned.
    /// </summary>
    public async Task<IReadOnlyList<SpotOccupancyEvent>> GetEventsAsync(
        Guid facilityId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var projection = new BsonDocument { { "_id", 0 }, { "eventId", 1 }, { "spotId", 1 }, { "type", 1 }, { "timestamp", 1 } };
        var documents = await collection.Find(FacilityRange(facilityId, since, until))
            .Project(projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return documents
            .Select(d => new SpotOccupancyEvent(
                d["eventId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                d["spotId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                Type(d["type"]),
                Timestamp(d["timestamp"])))
            .ToList();
    }

    private static BsonDocument FacilityRange(Guid facilityId, DateTimeOffset since, DateTimeOffset until) => new()
    {
        { "facilityId", Uuid(facilityId) },
        { "timestamp", new BsonDocument { { "$gte", new BsonDateTime(since.UtcDateTime) }, { "$lt", new BsonDateTime(until.UtcDateTime) } } },
    };

    private static OccupancyEventType Type(BsonValue value) =>
        value.AsString == "OCCUPIED" ? OccupancyEventType.Occupied : OccupancyEventType.Freed;

    private static DateTimeOffset Timestamp(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>Creates the three indexes of the log if they do not exist (idempotent on the server).</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (indexesReady)
        {
            return;
        }

        await indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (indexesReady)
            {
                return;
            }

            var keys = Builders<BsonDocument>.IndexKeys;
            CreateIndexModel<BsonDocument>[] models =
            [
                new(keys.Ascending("eventId"), new CreateIndexOptions { Name = EventIdIndexName, Unique = true }),
                new(
                    keys.Ascending("facilityId").Ascending("timestamp"),
                    new CreateIndexOptions { Name = FacilityTimestampIndexName }),

                // State of each spot before an aggregation range (UC-04, ADR-0010): one entry per spot (DISTINCT_SCAN).
                // A third index costs on every insert; measured in Phase 9 (RNF-01, RNF-05).
                new(
                    keys.Ascending("facilityId").Ascending("spotId").Descending("timestamp").Descending("eventId"),
                    new CreateIndexOptions { Name = FacilitySpotTimestampIndexName }),
            ];
            await collection.Indexes.CreateManyAsync(models, cancellationToken).ConfigureAwait(false);
            indexesReady = true;
        }
        finally
        {
            indexLock.Release();
        }
    }

    private static BsonDocument? ToDocument(OccupancyEventRecord record, DateTimeOffset storedAt)
    {
        BsonDocument payload;
        try
        {
            payload = BsonDocument.Parse(record.PayloadJson);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null; // not a JSON object: can never be stored
        }

        return new BsonDocument
        {
            { "eventId", Uuid(record.EventId) },
            { "sensorId", Uuid(record.SensorId) },
            { "spotId", Uuid(record.SpotId) },
            { "facilityId", Uuid(record.FacilityId) },
            { "type", record.Type == OccupancyEventType.Occupied ? "OCCUPIED" : "FREED" },
            { "timestamp", new BsonDateTime(record.Timestamp.UtcDateTime) },
            { "payload", payload },
            { "receivedAt", new BsonDateTime(record.ReceivedAt.UtcDateTime) },
            { "storedAt", new BsonDateTime(storedAt.UtcDateTime) },
        };
    }

    private static BsonBinaryData Uuid(Guid value) => new(value, GuidRepresentation.Standard);

    private static Guid EventId(BsonDocument document) => document["eventId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard);

    public void Dispose() => indexLock.Dispose();
}
