using FluentValidation;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Facilities;

/// <summary>
/// Rules shared by POST and PUT (spec 001, AC-17): coordinates in range, an IANA time zone, and at most one
/// interval per day with <c>opensAt</c> before <c>closesAt</c>. The limits come from the Domain.
/// </summary>
internal sealed class FacilityDetailsValidator : AbstractValidator<IFacilityDetailsRequest>
{
    public FacilityDetailsValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(ParkingFacility.NameMaxLength).OverridePropertyName("name");
        RuleFor(request => request.Address).NotEmpty().MaximumLength(ParkingFacility.AddressMaxLength).OverridePropertyName("address");

        RuleFor(request => request.Latitude)
            .InclusiveBetween(GeoCoordinate.MinLatitude, GeoCoordinate.MaxLatitude)
            .OverridePropertyName("latitude");
        RuleFor(request => request.Longitude)
            .InclusiveBetween(GeoCoordinate.MinLongitude, GeoCoordinate.MaxLongitude)
            .OverridePropertyName("longitude");

        RuleFor(request => request.TimeZone)
            .Must(IanaTimeZone.IsValid)
            .WithMessage("'timeZone' must be an IANA time zone identifier, for example Europe/Lisbon.")
            .OverridePropertyName("timeZone");

        RuleFor(request => request.OpeningHours)
            .NotNull()
            .Must(HaveDistinctDays).WithMessage("Each day of the week can appear only once.")
            .OverridePropertyName("openingHours");

        RuleForEach(request => request.OpeningHours)
            .ChildRules(hours =>
            {
                hours.RuleFor(entry => entry.DayOfWeek).InclusiveBetween(0, 6).OverridePropertyName("dayOfWeek");
                hours.RuleFor(entry => entry)
                    .Must(entry => FacilityOpeningHours.IsValidInterval(entry.OpensAt, entry.ClosesAt))
                    .WithMessage("'opensAt' must be earlier than 'closesAt'.")
                    .OverridePropertyName("opensAt");
            })
            .OverridePropertyName("openingHours");
    }

    private static bool HaveDistinctDays(IReadOnlyList<OpeningHoursRequest>? hours) =>
        hours is null || hours.Select(entry => entry.DayOfWeek).Distinct().Count() == hours.Count;
}
