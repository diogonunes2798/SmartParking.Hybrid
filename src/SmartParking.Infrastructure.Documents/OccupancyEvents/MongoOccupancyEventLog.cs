using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Application.Abstractions;
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

    /// <summary>Creates the two indexes of the log if they do not exist (idempotent on the server).</summary>
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
