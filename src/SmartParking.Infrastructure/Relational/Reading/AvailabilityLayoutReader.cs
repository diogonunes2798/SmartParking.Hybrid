using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>
/// The active configuration for the availability rebuild (spec 004, step 2): active facilities, their active spots and
/// the zones that hold them. Three <c>AsNoTracking</c> projections, grouped in memory; run once per rebuild, never by a
/// query. The location is projected whole (a converted value) and split into coordinates in memory.
/// </summary>
internal sealed class AvailabilityLayoutReader(SmartParkingDbContext db) : IAvailabilityLayoutReader
{
    public async Task<AvailabilityLayout> ReadAsync(CancellationToken cancellationToken)
    {
        var facilities = await db.ParkingFacilities.AsNoTracking()
            .Where(facility => facility.Status == FacilityStatus.Active)
            .Select(facility => new FacilityRow(facility.Id, facility.Name, facility.OperatorId, facility.Location))
            .ToListAsync(cancellationToken);

        var activeFacilities = db.ParkingFacilities.Where(facility => facility.Status == FacilityStatus.Active);

        var spots = await db.ParkingSpots.AsNoTracking()
            .Where(spot => spot.Status == SpotStatus.Active && activeFacilities.Any(facility => facility.Id == spot.FacilityId))
            .Select(spot => new SpotRow(spot.FacilityId, spot.Id, spot.ZoneId, spot.Type))
            .ToListAsync(cancellationToken);

        var zones = await db.Zones.AsNoTracking()
            .Where(zone => activeFacilities.Any(facility => facility.Id == zone.FacilityId))
            .Select(zone => new ZoneRow(zone.FacilityId, zone.Id, zone.Name))
            .ToListAsync(cancellationToken);

        var spotsByFacility = spots.ToLookup(spot => spot.FacilityId);
        var zonesByFacility = zones.ToLookup(zone => zone.FacilityId);

        return new AvailabilityLayout(
        [
            .. facilities.OrderBy(facility => facility.Id).Select(facility =>
            {
                var facilitySpots = spotsByFacility[facility.Id].OrderBy(spot => spot.Id).ToList();
                var occupiedZones = facilitySpots.Select(spot => spot.ZoneId).ToHashSet();

                return new FacilityLayout(
                    facility.Id,
                    facility.Name,
                    facility.OperatorId,
                    facility.Location.Latitude,
                    facility.Location.Longitude,
                    [.. zonesByFacility[facility.Id].Where(zone => occupiedZones.Contains(zone.Id)).OrderBy(zone => zone.Id)
                        .Select(zone => new ZoneLayout(zone.Id, zone.Name))],
                    [.. facilitySpots.Select(spot => new SpotLayout(spot.Id, spot.ZoneId, spot.Type))]);
            }),
        ]);
    }

    private sealed record FacilityRow(Guid Id, string Name, Guid OperatorId, GeoCoordinate Location);

    private sealed record SpotRow(Guid FacilityId, Guid Id, Guid ZoneId, SpotType Type);

    private sealed record ZoneRow(Guid FacilityId, Guid Id, string Name);
}
