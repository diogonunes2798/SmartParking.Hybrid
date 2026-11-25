using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Spots;

internal sealed class SpotFilterRequestValidator : AbstractValidator<SpotFilterRequest>
{
    public SpotFilterRequestValidator()
    {
        RuleFor(request => request.Status).MustBeNameOfWhenPresent<SpotFilterRequest, SpotStatus>("status");
    }
}
