using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Spots;

internal sealed class UpdateSpotRequestValidator : AbstractValidator<UpdateSpotRequest>
{
    public UpdateSpotRequestValidator()
    {
        RuleFor(request => request.ZoneId).NotEmpty().OverridePropertyName("zoneId");
        RuleFor(request => request.Code).NotEmpty().MaximumLength(ParkingSpot.CodeMaxLength).OverridePropertyName("code");
        RuleFor(request => request.Type).MustBeNameOf<UpdateSpotRequest, SpotType>("type");
        RuleFor(request => request.Status).MustBeNameOf<UpdateSpotRequest, SpotStatus>("status");
    }
}
