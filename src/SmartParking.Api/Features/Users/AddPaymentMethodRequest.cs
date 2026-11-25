using SmartParking.Application.Features.Users;
using SmartParking.Domain.Users;

namespace SmartParking.Api.Features.Users;

/// <summary>
/// Body of <c>POST /api/users/{id}/payment-methods</c>. The provider token is accepted here and never returned.
/// <see cref="ToString"/> leaves it out.
/// </summary>
internal sealed record AddPaymentMethodRequest(string? Type, string? ProviderToken, string? MaskedNumber, DateOnly? ExpiresOn)
{
    public AddPaymentMethod.Command ToCommand(Guid userId) =>
        new(userId, Enum.Parse<PaymentMethodType>(Type!), ProviderToken!, MaskedNumber!, ExpiresOn);

    public override string ToString() =>
        $"AddPaymentMethodRequest {{ Type = {Type}, MaskedNumber = {MaskedNumber}, ExpiresOn = {ExpiresOn} }}";
}
