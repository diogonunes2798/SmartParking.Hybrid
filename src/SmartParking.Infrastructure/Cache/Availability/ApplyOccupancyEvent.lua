-- Applies one occupancy event to the availability view (spec 004, «Ordem e idempotência»).
-- Read, comparison and write run in this one script, so two events of the same spot never interleave (AC-8).
--
-- KEYS[1]  avail:spot:{spotId}
-- ARGV[1]  event timestamp, Unix milliseconds
-- ARGV[2]  eventId, lowercase UUID text
-- ARGV[3]  new state: Free | Occupied
-- ARGV[4]  1 to publish a transition, 0 not to (rebuild)
-- ARGV[5]  pub/sub channel
-- ARGV[6]  spotId, lowercase UUID text
-- ARGV[7]  event timestamp, ISO 8601 (for the message)
-- ARGV[8]  facility key prefix (avail:facility:)
-- ARGV[9]  zone key prefix (avail:zone:)
--
-- The facility and zone keys are built from the spot hash: fine on a standalone Redis, not on a cluster (ADR 0010).
-- Returns applied | duplicate | stale | unknown_spot.

local spot = redis.call('HMGET', KEYS[1], 'facilityId', 'zoneId', 'type', 'state', 'lastTimestamp', 'lastEventId')
local facilityId, zoneId, spotType, state, lastTimestamp, lastEventId =
  spot[1], spot[2], spot[3], spot[4], spot[5], spot[6]

if not facilityId then
  return 'unknown_spot'
end

local timestamp = tonumber(ARGV[1])
local eventId = ARGV[2]

if lastEventId then
  if eventId == lastEventId then
    return 'duplicate'
  end
  local last = tonumber(lastTimestamp)
  -- Strictly greater pair wins: timestamp first, then eventId (byte order of the lowercase UUID text).
  if timestamp < last or (timestamp == last and eventId < lastEventId) then
    return 'stale'
  end
end

local newState = ARGV[3]
redis.call('HSET', KEYS[1], 'lastTimestamp', ARGV[1], 'lastEventId', eventId)

if state == newState then
  return 'applied'
end

redis.call('HSET', KEYS[1], 'state', newState)

local delta = -1
if newState == 'Free' then
  delta = 1
end

local typeField = 'free:' .. spotType
local facilityKey = ARGV[8] .. facilityId
local zoneKey = ARGV[9] .. zoneId
local facilityFree = redis.call('HINCRBY', facilityKey, 'free', delta)
redis.call('HINCRBY', facilityKey, typeField, delta)
local zoneFree = redis.call('HINCRBY', zoneKey, 'free', delta)
redis.call('HINCRBY', zoneKey, typeField, delta)

if ARGV[4] == '1' then
  redis.call('PUBLISH', ARGV[5], cjson.encode({
    spotId = ARGV[6],
    facilityId = facilityId,
    zoneId = zoneId,
    spotType = spotType,
    state = newState,
    facilityFree = facilityFree,
    zoneFree = zoneFree,
    timestamp = ARGV[7],
    eventId = eventId,
  }))
end

return 'applied'
