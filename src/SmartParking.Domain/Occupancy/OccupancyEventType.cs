namespace SmartParking.Domain.Occupancy;

/// <summary>What an occupancy sensor reports about its spot (spec 003). Stored by name.</summary>
public enum OccupancyEventType
{
    Occupied,
    Freed,
}
