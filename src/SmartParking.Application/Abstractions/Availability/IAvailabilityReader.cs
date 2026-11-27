using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Availability;

/// <summary>
/// Read port of the availability queries (spec 004, UC-03; RF-03, RF-04). No repository on the read side (D10): the
/// hybrid implementation reads only the Redis view, with a fixed number of round trips per call, never one per spot.
/// A spot type restricts every <c>free</c>/<c>total</c> to that type; a type absent from the view counts 0. A store
/// failure is an <see cref="ErrorType.Unavailable"/> error; there is no fallback to PostgreSQL.
/// </summary>
public interface IAvailabilityReader
{
    /// <summary>The facility, or an empty list when it is not in the view. A zone id keeps only that zone.</summary>
    Task<Result<IReadOnlyList<FacilityAvailability>>> GetFacilityAsync(
        Guid facilityId, SpotType? spotType, Guid? zoneId, CancellationToken cancellationToken);

    /// <summary>The operator's facilities in the view, in no particular order. A zone id keeps only that zone.</summary>
    Task<Result<IReadOnlyList<FacilityAvailability>>> GetOperatorFacilitiesAsync(
        Guid operatorId, SpotType? spotType, Guid? zoneId, CancellationToken cancellationToken);

    /// <summary>Every facility in the view within the radius of the point, unsorted.</summary>
    Task<Result<IReadOnlyList<NearbyFacility>>> SearchAsync(
        double latitude, double longitude, double radiusMeters, SpotType? spotType, CancellationToken cancellationToken);
}
