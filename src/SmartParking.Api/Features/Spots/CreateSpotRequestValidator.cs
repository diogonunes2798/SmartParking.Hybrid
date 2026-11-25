using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Spots;

internal sealed class CreateSpotRequestValidator : AbstractValidator<CreateSpotRequest>
{
    public CreateSpotRequestValidator()
    {
        RuleFor(request => request.ZoneId).NotEmpty().OverridePropertyName("zoneId");
        RuleFor(request => request.Code).NotEmpty().MaximumLength(ParkingSpot.CodeMaxLength).OverridePropertyName("code");
        RuleFor(request => request.Type).MustBeNameOf<CreateSpotRequest, SpotType>("type");
    }
}
