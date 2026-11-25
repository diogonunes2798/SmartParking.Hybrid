using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Domain.Users;

namespace SmartParking.Api.Features.Users;

internal sealed class AddPaymentMethodRequestValidator : AbstractValidator<AddPaymentMethodRequest>
{
    public AddPaymentMethodRequestValidator()
    {
        RuleFor(request => request.Type).MustBeNameOf<AddPaymentMethodRequest, PaymentMethodType>("type");
        RuleFor(request => request.ProviderToken)
            .NotEmpty()
            .MaximumLength(PaymentMethod.ProviderTokenMaxLength)
            .OverridePropertyName("providerToken");
        RuleFor(request => request.MaskedNumber)
            .NotEmpty()
            .MaximumLength(PaymentMethod.MaskedNumberMaxLength)
            .OverridePropertyName("maskedNumber");
    }
}
