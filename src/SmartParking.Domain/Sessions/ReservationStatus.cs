namespace SmartParking.Domain.Sessions;

/// <summary>Reservation lifecycle (stored as text). Only <c>Active</c> reservations take part in the overlap constraint.</summary>
public enum ReservationStatus
{
    Active,
    Cancelled,
    Fulfilled,
    Expired,
}
