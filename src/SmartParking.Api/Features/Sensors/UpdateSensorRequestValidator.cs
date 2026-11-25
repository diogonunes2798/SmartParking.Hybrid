using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Sensors;

internal sealed class UpdateSensorRequestValidator : AbstractValidator<UpdateSensorRequest>
{
    public UpdateSensorRequestValidator()
    {
        RuleFor(request => request.Model).NotEmpty().MaximumLength(Sensor.ModelMaxLength).OverridePropertyName("model");
        RuleFor(request => request.Status).MustBeNameOf<UpdateSensorRequest, SensorStatus>("status");
    }
}
