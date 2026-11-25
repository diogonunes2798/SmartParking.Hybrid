using FluentValidation;
using SmartParking.Domain.Users;

namespace SmartParking.Api.Features.Users;

internal sealed class RegisterVehicleRequestValidator : AbstractValidator<RegisterVehicleRequest>
{
    public RegisterVehicleRequestValidator()
    {
        RuleFor(request => request.LicensePlate)
            .Must(Vehicle.IsValidLicensePlate)
            .WithMessage($"'licensePlate' has only letters, digits, spaces and hyphens, and 1 to {Vehicle.LicensePlateMaxLength} letters and digits.")
            .OverridePropertyName("licensePlate");
    }
}
