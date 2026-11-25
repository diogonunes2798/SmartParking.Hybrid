using FluentValidation;

namespace SmartParking.Api.Features.Facilities;

internal sealed class CreateFacilityRequestValidator : AbstractValidator<CreateFacilityRequest>
{
    public CreateFacilityRequestValidator()
    {
        RuleFor(request => request.OperatorId).NotEmpty().OverridePropertyName("operatorId");
        Include(new FacilityDetailsValidator());
    }
}
