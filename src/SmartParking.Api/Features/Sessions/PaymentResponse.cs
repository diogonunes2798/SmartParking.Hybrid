using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Sessions;

/// <summary>Answer of a successful payment: the session, now <c>Paid</c>, and the payment id.</summary>
internal sealed record PaymentResponse(SessionResponse Session, Guid PaymentId)
{
    public static PaymentResponse From(PaymentReceipt receipt) => new(SessionResponse.From(receipt.Session), receipt.PaymentId);
}
