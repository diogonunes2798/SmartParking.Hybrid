using FluentValidation;

namespace SmartParking.Api.Features.Sessions;

internal sealed class OpenSessionRequestValidator : AbstractValidator<OpenSessionRequest>
{
    public OpenSessionRequestValidator()
    {
        RuleFor(request => request.SpotId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("spotId");
        RuleFor(request => request.VehicleId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("vehicleId");
        RuleFor(request => request.ReservationId).NotEqual(Guid.Empty).OverridePropertyName("reservationId");
    }
}
