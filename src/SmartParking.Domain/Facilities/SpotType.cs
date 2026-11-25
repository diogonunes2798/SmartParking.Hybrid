namespace SmartParking.Domain.Facilities;

/// <summary>Kind of vehicle a parking spot is meant for (stored as text).</summary>
public enum SpotType
{
    Standard,
    Accessible,
    ElectricVehicle,
    Motorcycle,
}
