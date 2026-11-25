namespace SmartParking.Domain.Facilities;

/// <summary>
/// Configuration status of a parking spot (stored as text). It is not occupancy:
/// free/occupied lives in Redis (F5) and is never stored in PostgreSQL (spec 001, AC-3).
/// </summary>
public enum SpotStatus
{
    Active,
    Inactive,
}
