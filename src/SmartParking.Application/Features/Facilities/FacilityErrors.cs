using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Facilities;

internal static class FacilityErrors
{
    public static Error NotFound(Guid id) =>
        Error.NotFound("Facility.NotFound", $"Parking facility {id} does not exist.");
}
