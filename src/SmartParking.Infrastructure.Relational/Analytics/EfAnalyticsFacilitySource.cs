using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Analytics;

namespace SmartParking.Infrastructure.Relational.Analytics;

/// <summary>
/// Facilities, zones and registered spots for the aggregation worker (ADR-0010). Reads the operational tables: this is
/// the write side of the analytical layer, not the report. Two queries without tracking, grouped in memory (D8: about
/// 5 000 spots).
/// </summary>
internal sealed class EfAnalyticsFacilitySource(SmartParkingDbContext context) : IAnalyticsFacilitySource
{
    public async Task<IReadOnlyList<AnalyticsFacility>> GetFacilitiesAsync(CancellationToken cancellationToken)
    {
        var facilities = await context.Facilities.AsNoTracking()
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.Zone })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var spots = (await context.Spots.AsNoTracking()
                .Select(s => new { s.Id, s.FacilityId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(s => s.FacilityId, s => s.Id);

        return facilities
            .Select(f => new AnalyticsFacility(f.Id, f.Zone, spots[f.Id].ToList()))
            .ToList();
    }
}
