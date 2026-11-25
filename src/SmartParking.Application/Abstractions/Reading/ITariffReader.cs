using SmartParking.Application.Abstractions.Paging;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read port of the tariff version queries (D10, ADR 0005).</summary>
public interface ITariffReader
{
    /// <summary>The version, or <c>null</c> when it does not exist in that facility.</summary>
    Task<TariffView?> GetAsync(Guid facilityId, Guid tariffId, CancellationToken cancellationToken);

    /// <summary>The version valid at <paramref name="at"/>, or <c>null</c> when the facility has none then (or does not exist).</summary>
    Task<TariffView?> GetCurrentAsync(Guid facilityId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>One page of the facility's versions, oldest first, or <c>null</c> when the facility does not exist.</summary>
    Task<PagedResult<TariffView>?> ListAsync(Guid facilityId, PageRequest page, CancellationToken cancellationToken);
}
