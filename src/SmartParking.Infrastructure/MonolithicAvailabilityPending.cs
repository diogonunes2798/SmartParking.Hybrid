using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure;

/// <summary>
/// Placeholder for the availability ports of the monolithic mode, which come in F9 (spec 004, ronda 3). It is never
/// called: the <c>ProjectionRebuilder</c> refuses the monolithic mode before any handler runs, and the availability
/// endpoints answer 501 (F5b). It exists only so the handlers can be built in both modes.
/// </summary>
internal sealed class MonolithicAvailabilityPending
    : IOccupancyProjector, IAvailabilityViewStore, ILatestOccupancyEventReader, IOccupancyEventIndexes
{
    private const string Message = "Availability in the monolithic mode arrives in F9.";

    public Task<ProjectionOutcome> ApplyAsync(OccupancyEvent occupancyEvent, bool publishChanges, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);

    public Task ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException(Message);

    public Task SeedAsync(AvailabilityLayout layout, CancellationToken cancellationToken) => throw new NotSupportedException(Message);

    public IAsyncEnumerable<OccupancyEvent> ReadLatestPerSpotAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);

    public Task EnsureAsync(CancellationToken cancellationToken) => throw new NotSupportedException(Message);
}
