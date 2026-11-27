using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Domain.Facilities;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>
/// Reads the availability view for the queries (spec 004, «API Contract»; D10: a read port, no repository). Each call
/// takes a fixed number of round trips, whatever the number of facilities, zones or spots: the hashes and sets of one
/// step go in a single pipelined batch.
/// <list type="bullet">
/// <item>by facility: (1) the facility hash and its zone set; (2) the zone hashes;</item>
/// <item>by operator: (1) the operator's facility set; then the two steps above;</item>
/// <item>radius search: (1) <c>GEOSEARCH … WITHDIST</c> with no <c>COUNT</c>, since sorting by free spots needs every
/// match; (2) the counters of the matched facilities.</item>
/// </list>
/// A field absent from a hash counts 0 (a spot type the facility does not have). A key missing while a rebuild runs
/// leaves its facility or zone out, and so does a hash without its name or operator (a counter the projector recreated
/// after the rebuild removed the hash). Only connection failures, timeouts and LOADING become <see cref="ErrorType.Unavailable"/>;
/// anything else is a bug and propagates.
/// </summary>
internal sealed class AvailabilityReader(RedisConnection redis) : IAvailabilityReader
{
    public Task<Result<IReadOnlyList<FacilityAvailability>>> GetFacilityAsync(
        Guid facilityId, SpotType? spotType, Guid? zoneId, CancellationToken cancellationToken) =>
        ReadAsync(database => ReadFacilitiesAsync(database, [facilityId], spotType, zoneId, cancellationToken), cancellationToken);

    public Task<Result<IReadOnlyList<FacilityAvailability>>> GetOperatorFacilitiesAsync(
        Guid operatorId, SpotType? spotType, Guid? zoneId, CancellationToken cancellationToken) =>
        ReadAsync(
            async database =>
            {
                var members = await database.SetMembersAsync(AvailabilityKeys.OperatorFacilities(operatorId)).WaitAsync(cancellationToken);
                return await ReadFacilitiesAsync(database, [.. members.Select(ToGuid)], spotType, zoneId, cancellationToken);
            },
            cancellationToken);

    public Task<Result<IReadOnlyList<NearbyFacility>>> SearchAsync(
        double latitude, double longitude, double radiusMeters, SpotType? spotType, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<NearbyFacility>>(
            async database =>
            {
                var matches = await database.GeoSearchAsync(
                    AvailabilityKeys.Geo,
                    longitude,
                    latitude,
                    new GeoSearchCircle(radiusMeters, GeoUnit.Meters),
                    options: GeoRadiusOptions.WithDistance).WaitAsync(cancellationToken);
                if (matches.Length == 0)
                {
                    return [];
                }

                var (free, total) = CounterFields(spotType);
                RedisValue[] fields = [AvailabilityKeys.NameField, AvailabilityKeys.OperatorIdField, free, total];
                var batch = database.CreateBatch();
                var reads = matches.Select(match => batch.HashGetAsync(AvailabilityKeys.FacilityPrefix + match.Member, fields)).ToArray();
                batch.Execute();
                await Task.WhenAll(reads).WaitAsync(cancellationToken);

                List<NearbyFacility> found = [];
                for (var index = 0; index < matches.Length; index++)
                {
                    var values = await reads[index];
                    if (values[0].IsNull || values[1].IsNull)
                    {
                        continue;
                    }

                    found.Add(new NearbyFacility(
                        ToGuid(matches[index].Member),
                        values[0].ToString(),
                        ToGuid(values[1]),
                        matches[index].Distance ?? 0,
                        ToCount(values[2]),
                        ToCount(values[3])));
                }

                return found;
            },
            cancellationToken);

    private static async Task<IReadOnlyList<FacilityAvailability>> ReadFacilitiesAsync(
        IDatabase database, IReadOnlyList<Guid> facilityIds, SpotType? spotType, Guid? zoneId, CancellationToken cancellationToken)
    {
        if (facilityIds.Count == 0)
        {
            return [];
        }

        // Step 1: every facility hash and zone set in one batch.
        var batch = database.CreateBatch();
        var facilityReads = facilityIds.Select(id => batch.HashGetAllAsync(AvailabilityKeys.Facility(id))).ToArray();
        var zoneSetReads = facilityIds.Select(id => batch.SetMembersAsync(AvailabilityKeys.FacilityZones(id))).ToArray();
        batch.Execute();
        await Task.WhenAll([.. facilityReads, .. zoneSetReads]).WaitAsync(cancellationToken);

        List<(Guid Id, string Name, Guid OperatorId, Dictionary<string, RedisValue> Hash, Guid[] ZoneIds)> facilities = [];
        for (var index = 0; index < facilityIds.Count; index++)
        {
            var hash = ToDictionary(await facilityReads[index]);
            if (!hash.TryGetValue(AvailabilityKeys.NameField, out var name) || !hash.TryGetValue(AvailabilityKeys.OperatorIdField, out var operatorId))
            {
                continue;
            }

            var zoneIds = (await zoneSetReads[index]).Select(ToGuid).Where(id => zoneId is not { } only || id == only).ToArray();
            facilities.Add((facilityIds[index], name.ToString(), ToGuid(operatorId), hash, zoneIds));
        }

        // Step 2: the zone hashes of every facility found, in one batch.
        var zoneIdsToRead = facilities.SelectMany(facility => facility.ZoneIds).Distinct().ToArray();
        var zones = new Dictionary<Guid, Dictionary<string, RedisValue>>();
        if (zoneIdsToRead.Length > 0)
        {
            batch = database.CreateBatch();
            var zoneReads = zoneIdsToRead.Select(id => batch.HashGetAllAsync(AvailabilityKeys.Zone(id))).ToArray();
            batch.Execute();
            await Task.WhenAll(zoneReads).WaitAsync(cancellationToken);
            for (var index = 0; index < zoneIdsToRead.Length; index++)
            {
                var hash = ToDictionary(await zoneReads[index]);
                if (hash.ContainsKey(AvailabilityKeys.NameField))
                {
                    zones[zoneIdsToRead[index]] = hash;
                }
            }
        }

        var (free, total) = CounterFields(spotType);
        return
        [
            .. facilities.Select(facility => new FacilityAvailability(
                facility.Id,
                facility.Name,
                facility.OperatorId,
                Count(facility.Hash, free),
                Count(facility.Hash, total),
                [
                    .. facility.ZoneIds
                        .Where(zones.ContainsKey)
                        .Select(id => new ZoneAvailability(
                            id,
                            zones[id][AvailabilityKeys.NameField].ToString(),
                            Count(zones[id], free),
                            Count(zones[id], total))),
                ])),
        ];
    }

    private async Task<Result<T>> ReadAsync<T>(Func<IDatabase, Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await redis.GetAsync(cancellationToken);
            return Result.Success(await read(connection.GetDatabase()));
        }
        catch (RedisConnectionException)
        {
            return Unavailable<T>();
        }
        catch (RedisTimeoutException)
        {
            return Unavailable<T>();
        }
        catch (RedisServerException exception) when (exception.Message.StartsWith("LOADING", StringComparison.Ordinal))
        {
            // Redis answers LOADING while it replays the AOF after a restart (compose runs it with --appendonly yes).
            return Unavailable<T>();
        }
    }

    private static Result<T> Unavailable<T>() =>
        Result.Failure<T>(Error.Unavailable("Availability.ViewUnavailable", "The availability view could not be read. Retry later."));

    /// <summary>The overall counters, or those of one spot type.</summary>
    private static (string Free, string Total) CounterFields(SpotType? spotType) =>
        spotType is { } type
            ? (AvailabilityKeys.FreeOf(type.ToString()), AvailabilityKeys.TotalOf(type.ToString()))
            : (AvailabilityKeys.FreeField, AvailabilityKeys.TotalField);

    private static Dictionary<string, RedisValue> ToDictionary(HashEntry[] hash) =>
        hash.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value, StringComparer.Ordinal);

    private static int Count(Dictionary<string, RedisValue> hash, string field) =>
        hash.TryGetValue(field, out var value) ? ToCount(value) : 0;

    private static int ToCount(RedisValue value) => value.IsNull ? 0 : (int)value;

    private static Guid ToGuid(RedisValue value) => Guid.Parse(value.ToString());
}
