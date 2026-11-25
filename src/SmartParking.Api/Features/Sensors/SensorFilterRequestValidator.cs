using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Sensors;

internal sealed class SensorFilterRequestValidator : AbstractValidator<SensorFilterRequest>
{
    public SensorFilterRequestValidator()
    {
        RuleFor(request => request.Kind).MustBeNameOfWhenPresent<SensorFilterRequest, SensorKind>("kind");
    }
}
