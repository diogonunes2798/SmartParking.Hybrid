using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Abstractions.Documents;

/// <summary>
/// Reads the last occupancy event of each spot from the event log (spec 004, rebuild step 3): the greatest
/// (timestamp, eventId) pair per spot. Read-only, and separate from <see cref="IOccupancyEventLog"/>, which only appends.
/// The <see cref="OccupancyEvent.Payload"/> of the events read is always null: the view does not use it.
/// </summary>
public interface ILatestOccupancyEventReader
{
    IAsyncEnumerable<OccupancyEvent> ReadLatestPerSpotAsync(CancellationToken cancellationToken);
}
