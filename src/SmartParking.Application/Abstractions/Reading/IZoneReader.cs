using SmartParking.Application.Abstractions.Paging;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read port of the zone queries (D10: no repository on the read side).</summary>
public interface IZoneReader
{
    /// <summary>The zone, or <c>null</c> when it does not exist in that facility.</summary>
    Task<ZoneView?> GetAsync(Guid facilityId, Guid zoneId, CancellationToken cancellationToken);

    /// <summary>One page of the facility's zones in creation order, or <c>null</c> when the facility does not exist.</summary>
    Task<PagedResult<ZoneView>?> ListAsync(Guid facilityId, PageRequest page, CancellationToken cancellationToken);
}
