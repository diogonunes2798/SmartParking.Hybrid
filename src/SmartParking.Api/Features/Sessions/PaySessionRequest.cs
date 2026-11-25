namespace SmartParking.Api.Features.Sessions;

/// <summary>Body of <c>POST /api/sessions/{id}/pay</c>: the payment method of the vehicle's owner.</summary>
internal sealed record PaySessionRequest(Guid? PaymentMethodId);
