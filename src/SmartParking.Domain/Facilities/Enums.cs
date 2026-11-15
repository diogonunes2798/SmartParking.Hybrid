namespace SmartParking.Domain.Facilities;

/// <summary>Kind of facility (spec §3: covered, uncovered, multi-storey).</summary>
public enum FacilityType
{
    Covered = 1,
    Uncovered = 2,
    MultiStorey = 3,
}

/// <summary>
/// Vehicle type a spot accepts. Also the <c>type</c> filter of UC-03.
/// TODO(Diogo): confirm the final list with Chapter 3 (the spec only says «tipo de viatura permitido»).
/// </summary>
public enum VehicleType
{
    Car = 1,
    Motorcycle = 2,
    Van = 3,
    Electric = 4,
    ReducedMobility = 5,
}

/// <summary>Operational state of a sensor (configuration data, RF-06).</summary>
public enum SensorStatus
{
    Active = 1,
    Inactive = 2,
    Maintenance = 3,
    Faulty = 4,
}
