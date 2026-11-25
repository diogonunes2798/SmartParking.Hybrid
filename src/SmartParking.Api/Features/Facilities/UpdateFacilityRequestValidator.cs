using FluentValidation;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Facilities;

internal sealed class UpdateFacilityRequestValidator : AbstractValidator<UpdateFacilityRequest>
{
    public UpdateFacilityRequestValidator()
    {
        Include(new FacilityDetailsValidator());

        RuleFor(request => request.Status)
            .Must(status => status is not null && Enum.GetNames<FacilityStatus>().Contains(status, StringComparer.Ordinal))
            .WithMessage($"'status' must be one of: {string.Join(", ", Enum.GetNames<FacilityStatus>())}.")
            .OverridePropertyName("status");
    }
}
