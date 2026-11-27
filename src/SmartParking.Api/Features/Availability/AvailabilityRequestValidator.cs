using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Availability;

internal sealed class AvailabilityRequestValidator : AbstractValidator<AvailabilityRequest>
{
    public AvailabilityRequestValidator()
    {
        RuleFor(request => request.FacilityId)
            .Must((request, facilityId) => facilityId is not null || request.OperatorId is not null)
            .WithMessage("'facilityId' or 'operatorId' is required.")
            .OverridePropertyName("facilityId");

        RuleFor(request => request.SpotType).MustBeNameOfWhenPresent<AvailabilityRequest, SpotType>("spotType");
    }
}
