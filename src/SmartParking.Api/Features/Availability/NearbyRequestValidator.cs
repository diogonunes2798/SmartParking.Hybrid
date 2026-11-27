using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Availability;

/// <summary>
/// Fixed limits of the radius search (spec 004, «API Contract»). The latitude follows the domain's
/// <see cref="GeoCoordinate"/> range, the one the Redis GEO index accepts (revision of 2026-11-27; ADR 0010).
/// </summary>
internal sealed class NearbyRequestValidator : AbstractValidator<NearbyRequest>
{
    public const double MaxRadiusMeters = 50_000;
    public const int MaxLimit = 100;

    public NearbyRequestValidator()
    {
        RuleFor(request => request.Lat)
            .NotNull().WithMessage("'lat' is required.")
            .Must(lat => lat is null || GeoCoordinate.IsValidLatitude(lat.Value))
            .WithMessage($"'lat' must be between {GeoCoordinate.MinLatitude} and {GeoCoordinate.MaxLatitude}.")
            .OverridePropertyName("lat");

        RuleFor(request => request.Lon)
            .NotNull().WithMessage("'lon' is required.")
            .InclusiveBetween(-180, 180).WithMessage("'lon' must be between -180 and 180.")
            .OverridePropertyName("lon");

        RuleFor(request => request.RadiusMeters)
            .NotNull().WithMessage("'radiusMeters' is required.")
            .InclusiveBetween(1, MaxRadiusMeters).WithMessage($"'radiusMeters' must be between 1 and {MaxRadiusMeters}.")
            .OverridePropertyName("radiusMeters");

        RuleFor(request => request.Sort)
            .Must(sort => sort is null or NearbyRequest.SortByDistance or NearbyRequest.SortByFree)
            .WithMessage($"'sort' must be one of: {NearbyRequest.SortByDistance}, {NearbyRequest.SortByFree}.")
            .OverridePropertyName("sort");

        RuleFor(request => request.Limit)
            .InclusiveBetween(1, MaxLimit).WithMessage($"'limit' must be between 1 and {MaxLimit}.")
            .OverridePropertyName("limit");

        RuleFor(request => request.SpotType).MustBeNameOfWhenPresent<NearbyRequest, SpotType>("spotType");
    }
}
