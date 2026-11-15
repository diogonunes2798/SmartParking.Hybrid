using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Sessions;
using SmartParking.Domain.Tariffs;

namespace SmartParking.Infrastructure.Relational.Persistence;

/// <summary>Tariff in force for a spot: spot → facility zone → tariff of that zone, in a single query.</summary>
internal sealed class EfTariffLookup(SmartParkingDbContext context) : ITariffLookup
{
    public Task<Tariff?> FindInForceForSpotAsync(Guid spotId, DateTimeOffset instant, CancellationToken cancellationToken) =>
        (from spot in context.Spots
         join facility in context.Facilities on spot.FacilityId equals facility.Id
         join tariff in context.Tariffs on facility.Zone equals tariff.Zone
         where spot.Id == spotId
             && tariff.Validity.ValidFrom <= instant
             && (tariff.Validity.ValidTo == null || tariff.Validity.ValidTo > instant)
         orderby tariff.Validity.ValidFrom descending, tariff.Id descending
         select tariff)
        .AsNoTracking()
        .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Query side of UC-02: no-tracking reads.</summary>
internal sealed class EfSessionQueries(SmartParkingDbContext context) : ISessionQueries
{
    public async Task<SessionDto?> GetSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return session is null ? null : SessionDto.From(session);
    }
}
