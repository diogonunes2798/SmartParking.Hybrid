using System.Globalization;
using SmartParking.Application.Availability;
using SmartParking.Domain.Facilities;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>
/// Availability read model in Redis (UC-03, RF-03; key model and scripts in ADR-0008). Writes go through the Lua
/// scripts of <see cref="ReadModelScripts"/> (atomic, idempotent, ordered, refused during a rebuild); reads use GEO
/// and hashes only. StackExchange.Redis sends a script with <c>EVAL</c> the first time and <c>EVALSHA</c> afterwards
/// (and again <c>EVAL</c> after a <c>NOSCRIPT</c>, e.g. a Redis restart), as seen with <c>INFO commandstats</c> in the
/// manual test of Phase 4.
/// </summary>
internal sealed class RedisAvailabilityReadModel(RedisCacheConnection connection, TimeProvider time)
    : IAvailabilityReadModel, IAvailabilityQueries
{
    /// <summary>
    /// How long a tombstone of a deleted spot or facility is kept (ADR-0008). Long enough for any late or redelivered
    /// configuration event of the same aggregate; it holds no counters, so its expiry cannot corrupt them.
    /// </summary>
    public static readonly TimeSpan TombstoneTtl = TimeSpan.FromHours(24);

    /// <summary>Rebuild lock expiry: if the rebuilding process dies, the projectors resume after at most this.</summary>
    public static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(2);

    /// <summary>The rebuild extends its lock at most this often (on its next write).</summary>
    private static readonly TimeSpan LockRenewal = TimeSpan.FromSeconds(30);

    private const string NoToken = "";

    private const string ExtendLock = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 0
        """;

    private const string ReleaseLock = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private static readonly RedisValue Prefix = ReadModelKeys.Prefix;
    private static readonly RedisValue Channel = ReadModelKeys.Channel;
    private static readonly long TombstoneTtlMs = (long)TombstoneTtl.TotalMilliseconds;

    private static readonly RedisValue[] FacilityFields =
        ["name", "zone", "type", "capacity", "lat", "lon", "spots", "free"];

    // ---- Projector side (no token: refused while a rebuild holds the lock) ----

    public Task<IReadOnlyList<ApplyOutcome>> ApplyAsync(IReadOnlyList<OccupancyChange> changes, CancellationToken cancellationToken) =>
        ApplyAsync(changes, NoToken, cancellationToken);

    public Task<ConfigurationOutcome> UpsertFacilityAsync(FacilitySnapshot facility, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
        UpsertFacilityAsync(facility, changedAt, NoToken, cancellationToken);

    public Task<ConfigurationOutcome> DeleteFacilityAsync(Guid facilityId, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
        DeleteFacilityAsync(facilityId, changedAt, NoToken, cancellationToken);

    public Task<ConfigurationOutcome> UpsertSpotAsync(SpotSnapshot spot, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
        UpsertSpotAsync(spot, changedAt, NoToken, cancellationToken);

    public Task<ConfigurationOutcome> DeleteSpotAsync(Guid spotId, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
        DeleteSpotAsync(spotId, changedAt, NoToken, cancellationToken);

    public async Task<DateTimeOffset?> GetLoadedAtAsync(CancellationToken cancellationToken)
    {
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var value = await db.HashGetAsync(ReadModelKeys.Meta, "loadedAt").ConfigureAwait(false);
        return value.IsNullOrEmpty
            ? null
            : DateTimeOffset.Parse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public async Task<IReadModelRebuildSession> BeginRebuildAsync(CancellationToken cancellationToken)
    {
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var token = Guid.NewGuid().ToString("N");
        if (!await db.StringSetAsync(ReadModelKeys.RebuildLock, token, LockTtl, When.NotExists).ConfigureAwait(false))
        {
            throw new RebuildInProgressException();
        }

        return new RebuildSession(this, token, time);
    }

    // ---- Writes with a token ----

    private async Task<IReadOnlyList<ApplyOutcome>> ApplyAsync(
        IReadOnlyList<OccupancyChange> changes, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return [];
        }

        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);

        // All scripts are sent before awaiting any reply (pipelining on the multiplexer); Redis runs them in the
        // order sent, one at a time, so the outcomes are those of a sequential application.
        var pending = new Task<RedisResult>[changes.Count];
        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            pending[i] = db.ScriptEvaluateAsync(
                ReadModelScripts.ApplyOccupancy,
                [ReadModelKeys.Spot(c.SpotId), ReadModelKeys.RebuildLock],
                [
                    Prefix,
                    c.TimestampMs,
                    ReadModelKeys.Id(c.EventId),
                    c.State == SpotState.Occupied ? "OCCUPIED" : "FREE",
                    Channel,
                    ReadModelKeys.Id(c.SpotId),
                    token,
                ]);
        }

        // WhenAll observes every task, so no failure is left unobserved; the first one is rethrown.
        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        var outcomes = new ApplyOutcome[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            outcomes[i] = (ApplyOutcome)Code(results[i]);
        }

        return outcomes;
    }

    private async Task<ConfigurationOutcome> UpsertFacilityAsync(
        FacilitySnapshot facility, DateTimeOffset changedAt, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facility);
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var result = await db.ScriptEvaluateAsync(
            ReadModelScripts.UpsertFacility,
            [ReadModelKeys.Facility(facility.FacilityId), ReadModelKeys.Geo, ReadModelKeys.RebuildLock],
            [
                Prefix,
                ReadModelKeys.Id(facility.FacilityId),
                facility.Name,
                Number(facility.Latitude),
                Number(facility.Longitude),
                facility.Capacity,
                facility.Type.ToString(),
                facility.Zone,
                Micros(changedAt),
                token,
            ]).ConfigureAwait(false);
        return (ConfigurationOutcome)Code(result);
    }

    private async Task<ConfigurationOutcome> DeleteFacilityAsync(
        Guid facilityId, DateTimeOffset changedAt, string token, CancellationToken cancellationToken)
    {
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var result = await db.ScriptEvaluateAsync(
            ReadModelScripts.DeleteFacility,
            [ReadModelKeys.Facility(facilityId), ReadModelKeys.Geo, ReadModelKeys.RebuildLock],
            [Prefix, ReadModelKeys.Id(facilityId), Micros(changedAt), TombstoneTtlMs, token]).ConfigureAwait(false);
        return (ConfigurationOutcome)Code(result);
    }

    private async Task<ConfigurationOutcome> UpsertSpotAsync(
        SpotSnapshot spot, DateTimeOffset changedAt, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spot);
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var result = await db.ScriptEvaluateAsync(
            ReadModelScripts.UpsertSpot,
            [ReadModelKeys.Spot(spot.SpotId), ReadModelKeys.RebuildLock],
            [
                Prefix,
                ReadModelKeys.Id(spot.SpotId),
                ReadModelKeys.Id(spot.FacilityId),
                spot.AllowedVehicleType.ToString(),
                Micros(changedAt),
                token,
            ]).ConfigureAwait(false);
        return (ConfigurationOutcome)Code(result);
    }

    private async Task<ConfigurationOutcome> DeleteSpotAsync(
        Guid spotId, DateTimeOffset changedAt, string token, CancellationToken cancellationToken)
    {
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var result = await db.ScriptEvaluateAsync(
            ReadModelScripts.DeleteSpot,
            [ReadModelKeys.Spot(spotId), ReadModelKeys.RebuildLock],
            [Prefix, ReadModelKeys.Id(spotId), Micros(changedAt), TombstoneTtlMs, token]).ConfigureAwait(false);
        return (ConfigurationOutcome)Code(result);
    }

    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        var redis = await connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var db = redis.GetDatabase();

        // Fail rather than "clear" nothing silently (review of Phase 4, cache finding 7).
        var primaries = redis.GetServers().Where(s => s.IsConnected && !s.IsReplica).ToList();
        if (primaries.Count == 0)
        {
            throw new InvalidOperationException("No connected Redis primary to clear the read model.");
        }

        // SCAN (never KEYS, which blocks the server) on every primary, then UNLINK in pages: the memory is freed in the
        // background. The rebuild lock is outside the prefix, so it is not deleted.
        foreach (var server in primaries)
        {
            var page = new List<object>(1000);
            await foreach (var key in server.KeysAsync(pattern: ReadModelKeys.Prefix + "*", pageSize: 1000)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                page.Add(key);
                if (page.Count == 1000)
                {
                    await db.ExecuteAsync("UNLINK", page).ConfigureAwait(false);
                    page.Clear();
                }
            }

            if (page.Count > 0)
            {
                await db.ExecuteAsync("UNLINK", page).ConfigureAwait(false);
            }
        }
    }

    private async Task MarkLoadedAsync(DateTimeOffset loadedAt, CancellationToken cancellationToken)
    {
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        await db.HashSetAsync(ReadModelKeys.Meta, "loadedAt", loadedAt.ToString("O", CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
    }

    // ---- Query side (UC-03) ----

    public async Task<IReadOnlyList<FacilityAvailability>> SearchAsync(AvailabilitySearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var db = await DatabaseAsync(cancellationToken).ConfigureAwait(false);

        // GEOSEARCH key FROMLONLAT <lon> <lat> BYRADIUS <r> m ASC COUNT <n> WITHDIST — longitude first.
        var hits = await db.GeoSearchAsync(
            ReadModelKeys.Geo,
            longitude: search.Longitude,
            latitude: search.Latitude,
            new GeoSearchCircle(search.RadiusMeters, GeoUnit.Meters),
            count: search.Limit,
            demandClosest: true,
            order: Order.Ascending,
            options: GeoRadiusOptions.WithDistance).ConfigureAwait(false);
        if (hits.Length == 0)
        {
            return [];
        }

        RedisValue[] fields = search.VehicleType is { } type
            ? [.. FacilityFields[..6], "spots:" + type, "free:" + type]
            : FacilityFields;

        // One HMGET per facility, pipelined.
        var reads = hits
            .Select(h => db.HashGetAsync(ReadModelKeys.Facility(Guid.Parse(h.Member.ToString())), fields))
            .ToArray();
        var values = await Task.WhenAll(reads).ConfigureAwait(false);

        var result = new List<FacilityAvailability>(hits.Length);
        for (var i = 0; i < hits.Length; i++)
        {
            var v = values[i];
            if (v[0].IsNull)
            {
                continue; // removed between GEOSEARCH and HMGET
            }

            var spots = Int(v[6]);
            if (search.VehicleType is not null && spots == 0)
            {
                continue; // no spot of the requested type in this facility
            }

            result.Add(new FacilityAvailability(
                Guid.Parse(hits[i].Member.ToString()),
                v[0].ToString(),
                v[1].ToString(),
                Enum.Parse<FacilityType>(v[2].ToString()),
                double.Parse(v[4].ToString(), CultureInfo.InvariantCulture),
                double.Parse(v[5].ToString(), CultureInfo.InvariantCulture),
                Int(v[3]),
                hits[i].Distance ?? 0,
                spots,
                Int(v[7])));
        }

        return result;
    }

    private async Task<IDatabase> DatabaseAsync(CancellationToken cancellationToken) =>
        (await connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).GetDatabase();

    /// <summary>Script result code; the rebuild barrier becomes an exception (transient for the projectors).</summary>
    private static int Code(RedisResult result)
    {
        var code = (int)result;
        return code == ReadModelScripts.Rebuilding ? throw new ReadModelRebuildingException() : code;
    }

    private static int Int(RedisValue value) => value.IsNullOrEmpty ? 0 : (int)value;

    /// <summary>Round-trippable invariant text: what is stored is exactly what is read back.</summary>
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Unix microseconds: exact in a Lua number (a double) until the year 2255, finer than one request.</summary>
    private static long Micros(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    /// <summary>
    /// Holder of the rebuild lock. Every write carries the token, so the scripts let it through the barrier; the lock
    /// is extended on the way (at most every 30 s) and released on dispose. If the lock was lost (e.g. the process
    /// stalled for longer than its TTL and another rebuild started), the next write fails instead of mixing two
    /// rebuilds.
    /// </summary>
    private sealed class RebuildSession(RedisAvailabilityReadModel owner, string token, TimeProvider time) : IReadModelRebuildSession
    {
        private DateTimeOffset lastExtended = time.GetUtcNow();
        private bool disposed;

        public async Task<IReadOnlyList<ApplyOutcome>> ApplyAsync(IReadOnlyList<OccupancyChange> changes, CancellationToken cancellationToken)
        {
            await KeepAsync(cancellationToken).ConfigureAwait(false);
            return await owner.ApplyAsync(changes, token, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ConfigurationOutcome> UpsertFacilityAsync(FacilitySnapshot facility, DateTimeOffset changedAt, CancellationToken cancellationToken)
        {
            await KeepAsync(cancellationToken).ConfigureAwait(false);
            return await owner.UpsertFacilityAsync(facility, changedAt, token, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ConfigurationOutcome> DeleteFacilityAsync(Guid facilityId, DateTimeOffset changedAt, CancellationToken cancellationToken)
        {
            await KeepAsync(cancellationToken).ConfigureAwait(false);
            return await owner.DeleteFacilityAsync(facilityId, changedAt, token, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ConfigurationOutcome> UpsertSpotAsync(SpotSnapshot spot, DateTimeOffset changedAt, CancellationToken cancellationToken)
        {
            await KeepAsync(cancellationToken).ConfigureAwait(false);
            return await owner.UpsertSpotAsync(spot, changedAt, token, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ConfigurationOutcome> DeleteSpotAsync(Guid spotId, DateTimeOffset changedAt, CancellationToken cancellationToken)
        {
            await KeepAsync(cancellationToken).ConfigureAwait(false);
            return await owner.DeleteSpotAsync(spotId, changedAt, token, cancellationToken).ConfigureAwait(false);
        }

        public async Task ClearAsync(CancellationToken cancellationToken)
        {
            await KeepAsync(cancellationToken, force: true).ConfigureAwait(false);
            await owner.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task MarkLoadedAsync(DateTimeOffset loadedAt, CancellationToken cancellationToken)
        {
            await KeepAsync(cancellationToken, force: true).ConfigureAwait(false);
            await owner.MarkLoadedAsync(loadedAt, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            var db = await owner.DatabaseAsync(CancellationToken.None).ConfigureAwait(false);
            await db.ScriptEvaluateAsync(ReleaseLock, [ReadModelKeys.RebuildLock], [token]).ConfigureAwait(false);
        }

        private async Task KeepAsync(CancellationToken cancellationToken, bool force = false)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = time.GetUtcNow();
            if (!force && now - lastExtended < LockRenewal)
            {
                return;
            }

            var db = await owner.DatabaseAsync(cancellationToken).ConfigureAwait(false);
            var extended = await db.ScriptEvaluateAsync(
                ExtendLock, [ReadModelKeys.RebuildLock], [token, (long)LockTtl.TotalMilliseconds]).ConfigureAwait(false);
            if ((int)extended != 1)
            {
                throw new RebuildInProgressException("The rebuild lock was lost (expired or taken by another rebuild); rebuild aborted.");
            }

            lastExtended = now;
        }
    }
}
