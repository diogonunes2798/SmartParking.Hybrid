namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>
/// Lua scripts of the read model (ADR-0008). Redis runs a script as one unit: no other command interleaves, so a
/// spot's state and the facility/zone/type counters can never disagree, even with several projectors on the same
/// stream. A <c>MULTI</c> transaction would not do: the decision (newer? state changed?) depends on values read in
/// the same step, which needs <c>WATCH</c> and client-side retries.
/// <para>
/// <b>Rebuild barrier</b> (review of Phase 4, finding 1). Every script receives the rebuild lock key as its last
/// <c>KEYS</c> entry and a token as its last <c>ARGV</c> entry. While the lock exists, only the holder of the token
/// (the rebuild) may write; every other caller gets <see cref="Rebuilding"/> and nothing is changed. Without it, a
/// projector's <c>HINCRBY</c> during the rebuild's <c>SCAN</c> recreated a facility hash with <c>free = -1</c> that
/// survived the clear, and the counters stayed wrong for good.
/// </para>
/// <para>
/// <b>Tombstones</b> (finding 5). A deleted spot or facility leaves a hash with only <c>deleted</c> and
/// <c>changedAt</c>, expiring after a TTL, so that a configuration event older than the delete, handled later by
/// another consumer, is recognised as stale instead of recreating it. Tombstones hold no counters, so their expiry
/// cannot make counters drift.
/// </para>
/// <para>
/// The scripts derive the facility and zone keys from the spot hash, so they touch keys not passed in
/// <c>KEYS</c>. That is fine on one Redis node (the D2/Phase 4 setup) but not in Redis Cluster, where every key of
/// a script must be declared and live in one slot; a cluster variant would need hash tags per facility and the
/// zone counters computed elsewhere (ADR-0008, consequences).
/// </para>
/// Return codes match <c>ApplyOutcome</c> / <c>ConfigurationOutcome</c>; <see cref="Rebuilding"/> is shared.
/// </summary>
internal static class ReadModelScripts
{
    /// <summary>Return code of every script when a rebuild holds the lock and the caller is not that rebuild.</summary>
    public const int Rebuilding = 6;

    /// <summary>
    /// KEYS[1] spot, KEYS[2] lock · ARGV prefix, ts (ms), eventId, state, channel, spotId, token.
    /// 1 applied · 2 unchanged · 3 duplicate · 4 stale · 5 unknown spot · 6 rebuilding.
    /// Order of events of one spot: (ts, eventId); equal ts with a smaller eventId is stale, so every replay of the
    /// same set of events ends in the same state, whatever the delivery order.
    /// </summary>
    public const string ApplyOccupancy = """
        local lock = redis.call('GET', KEYS[2])
        if lock and lock ~= ARGV[7] then return 6 end
        local spot = KEYS[1]
        local prefix, ts, eventId, state, channel, spotId = ARGV[1], tonumber(ARGV[2]), ARGV[3], ARGV[4], ARGV[5], ARGV[6]
        local cur = redis.call('HMGET', spot, 'facilityId', 'vehicleType', 'state', 'ts', 'eventId')
        if not cur[1] then return 5 end
        local curTs = tonumber(cur[4]) or -1
        local curId = cur[5] or ''
        if ts < curTs then return 4 end
        if ts == curTs then
          if eventId == curId then return 3 end
          if eventId < curId then return 4 end
        end
        redis.call('HSET', spot, 'ts', ARGV[2], 'eventId', eventId)
        if cur[3] == state then return 2 end
        redis.call('HSET', spot, 'state', state)
        local delta = 1
        if state == 'OCCUPIED' then delta = -1 end
        local facility = prefix .. 'facility:' .. cur[1]
        local free = redis.call('HINCRBY', facility, 'free', delta)
        local freeOfType = redis.call('HINCRBY', facility, 'free:' .. cur[2], delta)
        local zone = redis.call('HGET', facility, 'zone')
        if zone then
          redis.call('HINCRBY', prefix .. 'zone:' .. zone, 'free', delta)
          redis.call('HINCRBY', prefix .. 'zone:' .. zone, 'free:' .. cur[2], delta)
        end
        redis.call('PUBLISH', channel, cjson.encode({
          facilityId = cur[1], spotId = spotId, vehicleType = cur[2], state = state,
          free = free, freeOfType = freeOfType, timestamp = ARGV[2]
        }))
        return 1
        """;

    /// <summary>
    /// KEYS[1] spot, KEYS[2] lock · ARGV prefix, spotId, facilityId, vehicleType, changedAt (µs), token.
    /// 1 applied · 2 stale · 6 rebuilding. A new spot starts FREE with ts 0 (ADR-0008) and counts as one more spot
    /// and one more free spot of its facility, zone and type. An existing spot can only change vehicle type (the
    /// domain never moves a spot to another facility); its counters move from the old type to the new one. A spot
    /// of a deleted facility (tombstone) is stale.
    /// </summary>
    public const string UpsertSpot = """
        local lock = redis.call('GET', KEYS[2])
        if lock and lock ~= ARGV[6] then return 6 end
        local spot = KEYS[1]
        local prefix, spotId, vt, changedAt = ARGV[1], ARGV[2], ARGV[4], tonumber(ARGV[5])
        local cur = redis.call('HMGET', spot, 'facilityId', 'vehicleType', 'state', 'changedAt', 'deleted')
        if cur[5] and changedAt <= (tonumber(cur[4]) or -1) then return 2 end
        local facilityId = cur[1] or ARGV[3]
        local facility = prefix .. 'facility:' .. facilityId
        local f = redis.call('HMGET', facility, 'zone', 'deleted')
        if f[2] then return 2 end
        if cur[5] then
          redis.call('DEL', spot)
          cur = {false, false, false, false, false}
        end
        local zone = f[1]
        local function add(field, delta)
          redis.call('HINCRBY', facility, field, delta)
          if zone then redis.call('HINCRBY', prefix .. 'zone:' .. zone, field, delta) end
        end
        if cur[1] then
          if changedAt <= (tonumber(cur[4]) or -1) then return 2 end
          if cur[2] ~= vt then
            add('spots:' .. cur[2], -1)
            add('spots:' .. vt, 1)
            if cur[3] == 'FREE' then
              add('free:' .. cur[2], -1)
              add('free:' .. vt, 1)
            end
          end
          redis.call('HSET', spot, 'vehicleType', vt, 'changedAt', ARGV[5])
          return 1
        end
        redis.call('HSET', spot, 'facilityId', facilityId, 'vehicleType', vt, 'state', 'FREE',
          'ts', '0', 'eventId', '', 'changedAt', ARGV[5])
        redis.call('SADD', facility .. ':spots', spotId)
        add('spots', 1)
        add('spots:' .. vt, 1)
        add('free', 1)
        add('free:' .. vt, 1)
        return 1
        """;

    /// <summary>
    /// KEYS[1] spot, KEYS[2] lock · ARGV prefix, spotId, changedAt (µs), tombstone TTL (ms), token.
    /// 1 applied · 2 stale · 3 unknown · 6 rebuilding.
    /// </summary>
    public const string DeleteSpot = """
        local lock = redis.call('GET', KEYS[2])
        if lock and lock ~= ARGV[5] then return 6 end
        local spot = KEYS[1]
        local prefix, spotId, changedAt = ARGV[1], ARGV[2], tonumber(ARGV[3])
        local cur = redis.call('HMGET', spot, 'facilityId', 'vehicleType', 'state', 'changedAt')
        if not cur[1] then return 3 end
        if changedAt < (tonumber(cur[4]) or -1) then return 2 end
        local facility = prefix .. 'facility:' .. cur[1]
        local zone = redis.call('HGET', facility, 'zone')
        local function add(field, delta)
          redis.call('HINCRBY', facility, field, delta)
          if zone then redis.call('HINCRBY', prefix .. 'zone:' .. zone, field, delta) end
        end
        add('spots', -1)
        add('spots:' .. cur[2], -1)
        if cur[3] == 'FREE' then
          add('free', -1)
          add('free:' .. cur[2], -1)
        end
        redis.call('SREM', facility .. ':spots', spotId)
        redis.call('DEL', spot)
        redis.call('HSET', spot, 'deleted', '1', 'changedAt', ARGV[3])
        redis.call('PEXPIRE', spot, ARGV[4])
        return 1
        """;

    /// <summary>
    /// KEYS[1] facility, KEYS[2] geo, KEYS[3] lock · ARGV prefix, id, name, lat, lon, capacity, type, zone,
    /// changedAt (µs), token. 1 applied · 2 stale · 6 rebuilding.
    /// GEOADD receives longitude, then latitude, and runs <em>before</em> any other write: a script that fails half way
    /// keeps its earlier writes (Redis has no rollback), so the only command that can reject a value comes first
    /// (finding 7). When the zone changes (or is set for the first time, if spots arrived before their facility), the
    /// facility's counters move from the old zone to the new one.
    /// </summary>
    public const string UpsertFacility = """
        local lock = redis.call('GET', KEYS[3])
        if lock and lock ~= ARGV[10] then return 6 end
        local facility, geo = KEYS[1], KEYS[2]
        local prefix, id, zone, changedAt = ARGV[1], ARGV[2], ARGV[8], tonumber(ARGV[9])
        local cur = redis.call('HMGET', facility, 'zone', 'changedAt', 'deleted')
        if cur[2] and changedAt <= tonumber(cur[2]) then return 2 end
        redis.call('GEOADD', geo, ARGV[5], ARGV[4], id)
        if cur[3] then
          redis.call('DEL', facility)
          cur = {false, false, false}
        end
        redis.call('HSET', facility, 'id', id, 'name', ARGV[3], 'lat', ARGV[4], 'lon', ARGV[5],
          'capacity', ARGV[6], 'type', ARGV[7], 'changedAt', ARGV[9])
        if cur[1] ~= zone then
          local all = redis.call('HGETALL', facility)
          for i = 1, #all, 2 do
            local f = all[i]
            if f == 'spots' or f == 'free' or string.sub(f, 1, 6) == 'spots:' or string.sub(f, 1, 5) == 'free:' then
              local v = tonumber(all[i + 1])
              if cur[1] then redis.call('HINCRBY', prefix .. 'zone:' .. cur[1], f, -v) end
              redis.call('HINCRBY', prefix .. 'zone:' .. zone, f, v)
            end
          end
          redis.call('HSET', facility, 'zone', zone)
        end
        return 1
        """;

    /// <summary>
    /// KEYS[1] facility, KEYS[2] geo, KEYS[3] lock · ARGV prefix, id, changedAt (µs), tombstone TTL (ms), token.
    /// 1 applied · 2 stale · 3 unknown · 6 rebuilding.
    /// The relational FK only lets a facility without spots be deleted; spots still in the read model (their delete
    /// events not arrived yet) are removed with it, and their late delete events then report «unknown».
    /// </summary>
    public const string DeleteFacility = """
        local lock = redis.call('GET', KEYS[3])
        if lock and lock ~= ARGV[5] then return 6 end
        local facility, geo = KEYS[1], KEYS[2]
        local prefix, id, changedAt = ARGV[1], ARGV[2], tonumber(ARGV[3])
        if redis.call('EXISTS', facility) == 0 then return 3 end
        local cur = redis.call('HMGET', facility, 'zone', 'changedAt', 'deleted')
        if cur[3] then return 3 end
        if cur[2] and changedAt < tonumber(cur[2]) then return 2 end
        if cur[1] then
          local all = redis.call('HGETALL', facility)
          for i = 1, #all, 2 do
            local f = all[i]
            if f == 'spots' or f == 'free' or string.sub(f, 1, 6) == 'spots:' or string.sub(f, 1, 5) == 'free:' then
              redis.call('HINCRBY', prefix .. 'zone:' .. cur[1], f, -tonumber(all[i + 1]))
            end
          end
        end
        local spots = redis.call('SMEMBERS', facility .. ':spots')
        for _, s in ipairs(spots) do redis.call('DEL', prefix .. 'spot:' .. s) end
        redis.call('DEL', facility, facility .. ':spots')
        redis.call('ZREM', geo, id)
        redis.call('HSET', facility, 'deleted', '1', 'changedAt', ARGV[3])
        redis.call('PEXPIRE', facility, ARGV[4])
        return 1
        """;
}
