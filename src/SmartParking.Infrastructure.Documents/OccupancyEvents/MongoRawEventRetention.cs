using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using SmartParking.Application.Retention;

namespace SmartParking.Infrastructure.Documents.OccupancyEvents;

/// <summary>Settings of the MongoDB event log (section <c>Mongo</c>).</summary>
public sealed class MongoEventLogOptions
{
    /// <summary>
    /// <c>Mongo:EventsBlockCompressor</c> (Phase 10, ADR-0013): WiredTiger block compressor of <c>occupancy_events</c>
    /// (<c>snappy</c>, <c>zstd</c>, <c>zlib</c> or <c>none</c>), applied when the collection is created. Null: the server
    /// default (snappy).
    /// </summary>
    public string? BlockCompressor { get; set; }

    public void Validate()
    {
        if (BlockCompressor is { } compressor && !MongoOccupancyEventLog.BlockCompressors.Contains(compressor))
        {
            throw new InvalidOperationException(
                $"Mongo:EventsBlockCompressor must be one of {string.Join(", ", MongoOccupancyEventLog.BlockCompressors)}, got '{compressor}'.");
        }
    }
}

/// <summary>
/// Retention of the MongoDB log (Phase 10, ADR-0013). Removal is left to the server's TTL monitor (every 60 s), on the
/// <see cref="MongoOccupancyEventLog.TimestampTtlIndexName"/> index. A TTL index knows only a date, not whether a day is
/// aggregated or archived, so this class is the <b>guard</b>: <c>expireAfterSeconds</c> is kept at
/// <c>(now − horizon) + GuardMargin</c>, rounded up to whole minutes, with <c>collMod</c>. The TTL monitor then removes only
/// events older than the horizon minus the margin, until the next run moves it. With no horizon the index stays at
/// «never». If the Workers stop for longer than the margin, the monitor goes on and may pass the horizon: the margin is
/// the tolerated outage.
/// </summary>
public sealed class MongoRawEventRetention(
    IMongoDatabase database,
    MongoOccupancyEventLog log,
    RetentionOptions options,
    TimeProvider time) : IRawEventRetentionStore
{
    private static readonly JsonWriterSettings ArchiveJson = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson, Indent = false };

    private readonly IMongoCollection<BsonDocument> collection =
        database.GetCollection<BsonDocument>(MongoOccupancyEventLog.CollectionName);

    public async Task<DateTimeOffset?> GetEarliestTimestampAsync(CancellationToken cancellationToken)
    {
        await log.EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        var first = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(new BsonDocument("timestamp", 1))
            .Project(new BsonDocument { { "_id", 0 }, { "timestamp", 1 } })
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return first is null ? null : new DateTimeOffset(first["timestamp"].ToUniversalTime(), TimeSpan.Zero);
    }

    public async Task<IReadOnlyDictionary<DateTimeOffset, DateTimeOffset>> GetLastStoredAtPerHourAsync(
        DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        BsonDocument[] pipeline =
        [
            new("$match", Range(since, until)),
            new("$group", new BsonDocument
            {
                { "_id", new BsonDocument("$dateTrunc", new BsonDocument { { "date", "$timestamp" }, { "unit", "hour" } }) },
                { "last", new BsonDocument("$max", "$storedAt") },
            }),
        ];
        var result = new Dictionary<DateTimeOffset, DateTimeOffset>();
        using var cursor = await collection.AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var hour in cursor.Current)
            {
                result[new DateTimeOffset(hour["_id"].ToUniversalTime(), TimeSpan.Zero)] =
                    new DateTimeOffset(hour["last"].ToUniversalTime(), TimeSpan.Zero);
            }
        }

        return result;
    }

    public Task<long> CountAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
        collection.CountDocumentsAsync(Range(since, until), cancellationToken: cancellationToken);

    public async IAsyncEnumerable<string> ExportAsync(
        DateTimeOffset since, DateTimeOffset until, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cursor = await collection.Find(Range(since, until))
            .Sort(new BsonDocument("timestamp", 1))
            .ToCursorAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                yield return document.ToJson(ArchiveJson);
            }
        }
    }

    public async Task<EnforcementResult> EnforceAsync(DateTimeOffset? horizon, CancellationToken cancellationToken)
    {
        await log.EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        var wanted = ExpireAfterSeconds(horizon, time.GetUtcNow(), options.GuardMargin);
        var current = await CurrentExpireAfterSecondsAsync(cancellationToken).ConfigureAwait(false);
        if (current != wanted)
        {
            await database.RunCommandAsync<BsonDocument>(
                    new BsonDocument
                    {
                        { "collMod", MongoOccupancyEventLog.CollectionName },
                        {
                            "index", new BsonDocument
                            {
                                { "name", MongoOccupancyEventLog.TimestampTtlIndexName },
                                { "expireAfterSeconds", wanted },
                            }
                        },
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return new EnforcementResult(wanted, []);
    }

    /// <summary>
    /// The TTL value of the guard: <c>(now − horizon) + margin</c> rounded up to whole minutes (at most one <c>collMod</c> per
    /// minute; rounding to hours would leave up to one more hour of events than the monolithic DROP, which distorts the
    /// comparison), at most <see cref="MongoOccupancyEventLog.NeverExpireSeconds"/>; «never» without a horizon.
    /// </summary>
    public static long ExpireAfterSeconds(DateTimeOffset? horizon, DateTimeOffset now, TimeSpan margin)
    {
        if (horizon is not { } limit)
        {
            return MongoOccupancyEventLog.NeverExpireSeconds;
        }

        var seconds = (now - limit + margin).TotalSeconds;
        var minutes = Math.Ceiling(Math.Max(seconds, 0) / 60d);
        return (long)Math.Min(minutes * 60d, MongoOccupancyEventLog.NeverExpireSeconds);
    }

    /// <summary>The <c>expireAfterSeconds</c> of the TTL index as stored, or null if the index is missing.</summary>
    public async Task<long?> CurrentExpireAfterSecondsAsync(CancellationToken cancellationToken)
    {
        using var cursor = await collection.Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var index in await cursor.ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index["name"].AsString == MongoOccupancyEventLog.TimestampTtlIndexName)
            {
                return index.TryGetValue("expireAfterSeconds", out var value) ? value.ToInt64() : null;
            }
        }

        return null;
    }

    private static BsonDocument Range(DateTimeOffset since, DateTimeOffset until) =>
        new("timestamp", new BsonDocument { { "$gte", new BsonDateTime(since.UtcDateTime) }, { "$lt", new BsonDateTime(until.UtcDateTime) } });
}
