namespace SmartParking.Application.Abstractions.Reading;

/// <summary>
/// Reads the current configuration that shapes the availability view: active facilities with their zones and
/// active spots (spec 004, rebuild step 2). Used only by the rebuild, never by a query.
/// </summary>
public interface IAvailabilityLayoutReader
{
    Task<AvailabilityLayout> ReadAsync(CancellationToken cancellationToken);
}
