using FluentValidation;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Tariffs;

/// <summary>
/// AC-17: a negative price or a billing increment outside 1–60 minutes is 400 before the handler runs. A
/// <c>validFrom</c> in the past depends on the current instant and is checked by the handler (AC-9; plan 001b, point 5).
/// </summary>
internal sealed class CreateTariffRequestValidator : AbstractValidator<CreateTariffRequest>
{
    public CreateTariffRequestValidator()
    {
        RuleFor(request => request.ValidFrom).NotNull().OverridePropertyName("validFrom");
        RuleFor(request => request.PricePerHour).NotNull().GreaterThanOrEqualTo(0).OverridePropertyName("pricePerHour");
        RuleFor(request => request.BillingIncrementMinutes)
            .NotNull()
            .InclusiveBetween(Tariff.MinBillingIncrementMinutes, Tariff.MaxBillingIncrementMinutes)
            .OverridePropertyName("billingIncrementMinutes");
        RuleFor(request => request.DailyMaximum).GreaterThanOrEqualTo(0).When(request => request.DailyMaximum is not null)
            .OverridePropertyName("dailyMaximum");
        RuleFor(request => request.FreeMinutes).NotNull().GreaterThanOrEqualTo(0).OverridePropertyName("freeMinutes");
        RuleFor(request => request.Currency)
            .Must(Tariff.IsValidCurrency)
            .When(request => request.Currency is not null)
            .WithMessage("'currency' must be an ISO 4217 code in upper case, for example EUR.")
            .OverridePropertyName("currency");
    }
}
