using SmartParking.Application.Features.Facilities;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Facilities;

/// <summary>Body of <c>PUT /api/facilities/{id}</c>: every field of the POST except the operator, plus the status.</summary>
internal sealed record UpdateFacilityRequest(
    string? Name,
    string? Address,
    double Latitude,
    double Longitude,
    string? TimeZone,
    string? Status,
    IReadOnlyList<OpeningHoursRequest>? OpeningHours) : IFacilityDetailsRequest
{
    public UpdateFacility.Command ToCommand(Guid id, uint? expectedVersion) => new(
        id,
        expectedVersion,
        Name!,
        Address!,
        Latitude,
        Longitude,
        TimeZone!,
        Enum.Parse<FacilityStatus>(Status!),
        OpeningHours!.ToInputs());
}
