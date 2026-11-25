using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Sensors;

/// <summary>AC-14: an environmental sensor cannot have a spot (400 before the handler runs).</summary>
internal sealed class RegisterSensorRequestValidator : AbstractValidator<RegisterSensorRequest>
{
    public RegisterSensorRequestValidator()
    {
        RuleFor(request => request.FacilityId).NotEmpty().OverridePropertyName("facilityId");
        RuleFor(request => request.HardwareId).NotEmpty().MaximumLength(Sensor.HardwareIdMaxLength).OverridePropertyName("hardwareId");
        RuleFor(request => request.Kind).MustBeNameOf<RegisterSensorRequest, SensorKind>("kind");
        RuleFor(request => request.Model).NotEmpty().MaximumLength(Sensor.ModelMaxLength).OverridePropertyName("model");
        RuleFor(request => request.InstalledAt).NotNull().OverridePropertyName("installedAt");
        RuleFor(request => request.SpotId)
            .Null()
            .When(request => request.Kind != nameof(SensorKind.Occupancy))
            .WithMessage("Only occupancy sensors can be placed on a spot.")
            .OverridePropertyName("spotId");
    }
}
