using FluentValidation;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Zones;

internal sealed class ZoneRequestValidator : AbstractValidator<ZoneRequest>
{
    public ZoneRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(Zone.NameMaxLength).OverridePropertyName("name");
    }
}
