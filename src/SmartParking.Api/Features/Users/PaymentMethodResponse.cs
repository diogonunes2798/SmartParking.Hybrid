using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Users;

/// <summary>A payment method on the wire. It has no provider token, on purpose (spec 002, AC-4).</summary>
internal sealed record PaymentMethodResponse(Guid Id, Guid UserId, string Type, string MaskedNumber, DateOnly? ExpiresOn)
{
    public static PaymentMethodResponse From(PaymentMethodView view) =>
        new(view.Id, view.UserId, view.Type.ToString(), view.MaskedNumber, view.ExpiresOn);
}
