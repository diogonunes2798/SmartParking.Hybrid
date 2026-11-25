namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Result of a successful payment: the session, now <c>Paid</c>, and the succeeded payment.</summary>
public sealed record PaymentReceipt(SessionView Session, Guid PaymentId);
