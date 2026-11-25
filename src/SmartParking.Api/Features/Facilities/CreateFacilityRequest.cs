using SmartParking.Application.Features.Facilities;

namespace SmartParking.Api.Features.Facilities;

/// <summary>Body of <c>POST /api/facilities</c>. Fields are nullable so a missing one is a 400, not a binding error.</summary>
internal sealed record CreateFacilityRequest(
    Guid OperatorId,
    string? Name,
    string? Address,
    double Latitude,
    double Longitude,
    string? TimeZone,
    IReadOnlyList<OpeningHoursRequest>? OpeningHours) : IFacilityDetailsRequest
{
    public CreateFacility.Command ToCommand() => new(
        OperatorId,
        Name!,
        Address!,
        Latitude,
        Longitude,
        TimeZone!,
        OpeningHours!.ToInputs());
}
