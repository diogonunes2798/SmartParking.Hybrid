namespace SmartParking.Application.Abstractions.Payments;

/// <summary>Answer of the payment gateway to a charge.</summary>
public enum PaymentOutcome
{
    Approved,
    Declined,
}
