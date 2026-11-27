using System.Globalization;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Reading;
using StackExchange.Redis;

namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>
/// Clears and seeds the availability view during a rebuild (spec 004, «Rebuild», steps 1 and 2). The clear walks the
/// <c>avail:*</c> keys of the configured database with <c>SCAN</c> and removes them with <c>UNLINK</c>, so no other key
/// is touched (AC-11). The seed writes one batch per facility, with every spot free and no event pair.
/// </summary>
internal sealed class AvailabilityViewStore(RedisConnection redis) : IAvailabilityViewStore
{
    private const int ScanPageSize = 1000;
    private const int UnlinkBatchSize = 500;

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        var connection = await redis.GetAsync(cancellationToken);
        var database = connection.GetDatabase();

        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            List<object> pending = [];
            await foreach (var key in server.KeysAsync(database.Database, AvailabilityKeys.Prefix + "*", ScanPageSize)
                .WithCancellation(cancellationToken))
            {
                pending.Add(key);
                if (pending.Count == UnlinkBatchSize)
                {
                    await database.ExecuteAsync("UNLINK", pending).WaitAsync(cancellationToken);
                    pending.Clear();
                }
            }

            if (pending.Count > 0)
            {
                await database.ExecuteAsync("UNLINK", pending).WaitAsync(cancellationToken);
            }
        }
    }

    public async Task SeedAsync(AvailabilityLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var connection = await redis.GetAsync(cancellationToken);
        var database = connection.GetDatabase();

        foreach (var facility in layout.Facilities)
        {
            var batch = database.CreateBatch();
            List<Task> writes = [];

            foreach (var spot in facility.Spots)
            {
                writes.Add(batch.HashSetAsync(AvailabilityKeys.Spot(spot.Id),
                [
                    new(AvailabilityKeys.FacilityIdField, AvailabilityKeys.Id(facility.Id)),
                    new(AvailabilityKeys.ZoneIdField, AvailabilityKeys.Id(spot.ZoneId)),
                    new(AvailabilityKeys.TypeField, spot.Type.ToString()),
                    new(AvailabilityKeys.StateField, AvailabilityKeys.Free),
                ]));
            }

            List<HashEntry> facilityEntries =
            [
                new(AvailabilityKeys.NameField, facility.Name),
                new(AvailabilityKeys.OperatorIdField, AvailabilityKeys.Id(facility.OperatorId)),
                new(AvailabilityKeys.LatitudeField, facility.Latitude.ToString("R", CultureInfo.InvariantCulture)),
                new(AvailabilityKeys.LongitudeField, facility.Longitude.ToString("R", CultureInfo.InvariantCulture)),
                .. Counters(facility.Spots),
            ];
            writes.Add(batch.HashSetAsync(AvailabilityKeys.Facility(facility.Id), [.. facilityEntries]));

            foreach (var zone in facility.Zones)
            {
                List<HashEntry> zoneEntries =
                [
                    new(AvailabilityKeys.FacilityIdField, AvailabilityKeys.Id(facility.Id)),
                    new(AvailabilityKeys.NameField, zone.Name),
                    .. Counters(facility.Spots.Where(spot => spot.ZoneId == zone.Id)),
                ];
                writes.Add(batch.HashSetAsync(AvailabilityKeys.Zone(zone.Id), [.. zoneEntries]));
            }

            if (facility.Zones.Count > 0)
            {
                writes.Add(batch.SetAddAsync(
                    AvailabilityKeys.FacilityZones(facility.Id),
                    [.. facility.Zones.Select(zone => (RedisValue)AvailabilityKeys.Id(zone.Id))]));
            }

            writes.Add(batch.SetAddAsync(AvailabilityKeys.OperatorFacilities(facility.OperatorId), AvailabilityKeys.Id(facility.Id)));
            writes.Add(batch.GeoAddAsync(AvailabilityKeys.Geo, facility.Longitude, facility.Latitude, AvailabilityKeys.Id(facility.Id)));

            batch.Execute();
            await Task.WhenAll(writes).WaitAsync(cancellationToken);
        }
    }

    /// <summary>Every spot starts free, so <c>free</c> = <c>total</c>, overall and per spot type present.</summary>
    private static IEnumerable<HashEntry> Counters(IEnumerable<SpotLayout> spots)
    {
        var byType = spots.GroupBy(spot => spot.Type.ToString()).Select(group => (Type: group.Key, Count: group.Count())).ToList();
        var total = byType.Sum(entry => entry.Count);

        yield return new(AvailabilityKeys.FreeField, total);
        yield return new(AvailabilityKeys.TotalField, total);
        foreach (var (type, count) in byType)
        {
            yield return new(AvailabilityKeys.FreeOf(type), count);
            yield return new(AvailabilityKeys.TotalOf(type), count);
        }
    }
}
