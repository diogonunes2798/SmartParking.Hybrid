using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Facilities;

/// <summary>Requested opening interval of one day; validated at the API boundary.</summary>
public sealed record OpeningHoursInput(DayOfWeek DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt)
{
    public FacilityOpeningHours ToDomain() => new(DayOfWeek, OpensAt, ClosesAt);
}
