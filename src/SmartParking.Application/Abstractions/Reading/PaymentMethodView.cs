using SmartParking.Domain.Users;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a payment method. It has no provider token, on purpose (spec 002, AC-4).</summary>
public sealed record PaymentMethodView(Guid Id, Guid UserId, PaymentMethodType Type, string MaskedNumber, DateOnly? ExpiresOn)
{
    public static PaymentMethodView From(PaymentMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return new PaymentMethodView(method.Id, method.UserId, method.Type, method.MaskedNumber, method.ExpiresOn);
    }
}
