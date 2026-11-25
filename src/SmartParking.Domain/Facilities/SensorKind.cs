namespace SmartParking.Domain.Facilities;

/// <summary>What a sensor measures (stored as text). Only occupancy sensors can sit on a spot.</summary>
public enum SensorKind
{
    Occupancy,
    Environmental,
}
