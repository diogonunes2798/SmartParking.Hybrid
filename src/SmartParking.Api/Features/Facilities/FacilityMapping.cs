using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;

namespace SmartParking.Api.Features.Facilities;

/// <summary>Conversions between the API contract and the Application types. Domain entities never reach the wire.</summary>
internal static class FacilityMapping
{
    public static IReadOnlyList<OpeningHoursInput> ToInputs(this IReadOnlyList<OpeningHoursRequest> hours) =>
        [.. hours.Select(entry => new OpeningHoursInput((DayOfWeek)entry.DayOfWeek, entry.OpensAt, entry.ClosesAt))];

    public static FacilityResponse ToResponse(this FacilityView view) => new(
        view.Id,
        view.OperatorId,
        view.Name,
        view.Address,
        view.Latitude,
        view.Longitude,
        view.TimeZone,
        view.Status.ToString(),
        [.. view.OpeningHours.Select(hours => new OpeningHoursResponse((int)hours.DayOfWeek, hours.OpensAt, hours.ClosesAt))]);

    public static PagedResponse<FacilityResponse> ToResponse(this PagedResult<FacilityView> page) => new(
        [.. page.Items.Select(ToResponse)],
        page.Page,
        page.PageSize,
        page.TotalCount);
}
