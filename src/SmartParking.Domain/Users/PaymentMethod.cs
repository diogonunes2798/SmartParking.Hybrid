using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users;

/// <summary>
/// Payment method of a user, held as the payment provider's token and a masked number.
/// The card number (PAN) and the CVV are never stored.
/// </summary>
public sealed class PaymentMethod
{
    public const int ProviderTokenMaxLength = 200;

    public const int MaskedNumberMaxLength = 32;

    private PaymentMethod()
    {
    }

    public Guid Id { get; private init; }

    public Guid UserId { get; private init; }

    public PaymentMethodType Type { get; private init; }

    public string ProviderToken { get; private init; } = string.Empty;

    public string MaskedNumber { get; private init; } = string.Empty;

    public DateOnly? ExpiresOn { get; private init; }

    public static PaymentMethod Create(
        Guid id,
        Guid userId,
        PaymentMethodType type,
        string providerToken,
        string maskedNumber,
        DateOnly? expiresOn) => new()
        {
            Id = Guard.Id(id),
            UserId = Guard.Id(userId),
            Type = Guard.Defined(type),
            ProviderToken = Guard.Text(providerToken, ProviderTokenMaxLength),
            MaskedNumber = Guard.Text(maskedNumber, MaskedNumberMaxLength),
            ExpiresOn = expiresOn,
        };
}
