using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a parking facility. <see cref="Version"/> is the <c>xmin</c>, sent as the ETag.</summary>
public sealed record FacilityView(
    Guid Id,
    Guid OperatorId,
    string Name,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZone,
    FacilityStatus Status,
    IReadOnlyList<OpeningHoursView> OpeningHours,
    uint Version)
{
    public static FacilityView From(ParkingFacility facility)
    {
        ArgumentNullException.ThrowIfNull(facility);

        return new FacilityView(
            facility.Id,
            facility.OperatorId,
            facility.Name,
            facility.Address,
            facility.Location.Latitude,
            facility.Location.Longitude,
            facility.TimeZone,
            facility.Status,
            [.. facility.OpeningHours
                .OrderBy(hours => hours.DayOfWeek)
                .Select(hours => new OpeningHoursView(hours.DayOfWeek, hours.OpensAt, hours.ClosesAt))],
            facility.Version);
    }
}
