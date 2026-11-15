using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>
/// Key model of the availability read model (ADR-0008). Every key starts with <see cref="Prefix"/>, so that a
/// rebuild can delete exactly the read model (<c>SCAN … MATCH sp:rm:*</c>) and nothing else in the same Redis (the
/// bus streams of ADR-0006 live there too).
/// <code>
/// sp:rm:spot:{spotId}               hash  facilityId, vehicleType, state (FREE|OCCUPIED), ts (ms), eventId, changedAt (µs)
/// sp:rm:facility:{facilityId}       hash  id, name, lat, lon, capacity, type, zone, changedAt (µs),
///                                         spots, free, spots:{VehicleType}, free:{VehicleType}
/// sp:rm:facility:{facilityId}:spots set   spot ids (deletes, diagnostics)
/// sp:rm:zone:{zone}                 hash  spots, free, spots:{VehicleType}, free:{VehicleType}
/// sp:rm:geo:facilities              GEO   member = facilityId (GEOADD takes LONGITUDE first)
/// sp:rm:meta                        hash  loadedAt
/// channel sp:availability           pub/sub, one JSON message per state change
/// sp:rebuild-lock                   string token of the rebuild holding the read model (outside the prefix: a clear
///                                   must not delete it)
/// </code>
/// A deleted spot or facility keeps its key as a tombstone (<c>deleted</c>, <c>changedAt</c>, with a TTL).
/// </summary>
public static class ReadModelKeys
{
    public const string Prefix = "sp:rm:";

    /// <summary>Pub/sub channel of state changes (UC-01 step 4).</summary>
    public const string Channel = "sp:availability";

    public static RedisKey Geo { get; } = Prefix + "geo:facilities";

    public static RedisKey Meta { get; } = Prefix + "meta";

    /// <summary>Rebuild lock and barrier (review of Phase 4, finding 1).</summary>
    public static RedisKey RebuildLock { get; } = "sp:rebuild-lock";

    public static RedisKey Spot(Guid spotId) => Prefix + "spot:" + Id(spotId);

    public static RedisKey Facility(Guid facilityId) => Prefix + "facility:" + Id(facilityId);

    public static RedisKey Zone(string zone) => Prefix + "zone:" + zone;

    /// <summary>GUIDs in the canonical lower-case "D" form (also the tie-break order of events in the Lua script).</summary>
    public static string Id(Guid id) => id.ToString("D");
}
