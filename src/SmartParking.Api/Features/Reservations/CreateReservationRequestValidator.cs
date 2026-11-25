using FluentValidation;

namespace SmartParking.Api.Features.Reservations;

/// <summary>Shape only (400). The time rules that need now or the facility are the handler's (spec 002, AC-23).</summary>
internal sealed class CreateReservationRequestValidator : AbstractValidator<CreateReservationRequest>
{
    public CreateReservationRequestValidator()
    {
        RuleFor(request => request.SpotId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("spotId");
        RuleFor(request => request.UserId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("userId");
        RuleFor(request => request.VehicleId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("vehicleId");
        RuleFor(request => request.StartsAt).NotNull().OverridePropertyName("startsAt");
        RuleFor(request => request.EndsAt)
            .NotNull()
            .GreaterThan(request => request.StartsAt)
            .When(request => request.StartsAt is not null)
            .WithMessage("Must be after startsAt.")
            .OverridePropertyName("endsAt");
    }
}
