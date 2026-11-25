using FluentValidation;

namespace SmartParking.Api.Features.Sensors;

internal sealed class AssignSensorRequestValidator : AbstractValidator<AssignSensorRequest>
{
    public AssignSensorRequestValidator()
    {
        RuleFor(request => request.SpotId).NotEmpty().OverridePropertyName("spotId");
    }
}
