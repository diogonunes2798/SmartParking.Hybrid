using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Availability;

namespace SmartParking.Infrastructure.Relational.Persistence;

/// <summary>
/// Configuration snapshot for the read model rebuild (ADR-0008): every facility and spot, streamed without tracking
/// (one pass, constant memory). Not paged: a rebuild reads everything once.
/// </summary>
internal sealed class EfAvailabilitySource(SmartParkingDbContext context) : IAvailabilitySource
{
    public IAsyncEnumerable<FacilitySnapshot> GetFacilitiesAsync(CancellationToken cancellationToken) =>
        context.Facilities.AsNoTracking()
            .Select(f => new FacilitySnapshot(
                f.Id, f.Name, f.Location.Latitude, f.Location.Longitude, f.Capacity, f.Type, f.Zone))
            .AsAsyncEnumerable();

    public IAsyncEnumerable<SpotSnapshot> GetSpotsAsync(CancellationToken cancellationToken) =>
        context.Spots.AsNoTracking()
            .Select(s => new SpotSnapshot(s.Id, s.FacilityId, s.AllowedVehicleType))
            .AsAsyncEnumerable();
}
