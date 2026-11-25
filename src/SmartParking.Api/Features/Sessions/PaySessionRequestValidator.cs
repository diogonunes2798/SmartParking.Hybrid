using FluentValidation;

namespace SmartParking.Api.Features.Sessions;

internal sealed class PaySessionRequestValidator : AbstractValidator<PaySessionRequest>
{
    public PaySessionRequestValidator()
    {
        RuleFor(request => request.PaymentMethodId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("paymentMethodId");
    }
}
