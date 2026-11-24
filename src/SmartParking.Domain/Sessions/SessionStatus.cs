namespace SmartParking.Domain.Sessions;

/// <summary>Parking session lifecycle (stored as text).</summary>
public enum SessionStatus
{
    Open,
    Closed,
    Paid,
}
