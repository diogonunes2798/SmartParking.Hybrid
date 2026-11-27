using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Abstractions.Availability;

/// <summary>Writes the layout of the availability view during a rebuild (spec 004, «Rebuild»). Not used by queries.</summary>
public interface IAvailabilityViewStore
{
    /// <summary>Removes every key of the view and nothing else.</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>Writes facilities, zones, spots, totals and locations, with every spot free and no event applied.</summary>
    Task SeedAsync(AvailabilityLayout layout, CancellationToken cancellationToken);
}
