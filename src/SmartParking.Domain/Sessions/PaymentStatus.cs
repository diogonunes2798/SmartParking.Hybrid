namespace SmartParking.Domain.Sessions;

/// <summary>Payment outcome (stored as text). A session has at most one <c>Succeeded</c> payment.</summary>
public enum PaymentStatus
{
    Pending,
    Succeeded,
    Failed,
}
