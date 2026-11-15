using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>Parking facility (spec §3, Cap. 3.5). Configuration data, stored relationally (RF-06).</summary>
public sealed class ParkingFacility : IVersioned
{
    public const int NameMaxLength = 200;
    public const int OperatorMaxLength = 200;
    public const int ZoneMaxLength = 100;

    private ParkingFacility()
    {
        Name = Operator = Zone = string.Empty;
        Location = null!;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public GeoPoint Location { get; private set; }

    /// <summary>Declared capacity (number of spaces), independent of how many spots are registered.</summary>
    public int Capacity { get; private set; }

    public FacilityType Type { get; private set; }

    public string Operator { get; private set; }

    public string Zone { get; private set; }

    public uint Version { get; private set; }

    public static ParkingFacility Create(
        string name, GeoPoint location, int capacity, FacilityType type, string @operator, string zone)
    {
        var facility = new ParkingFacility { Id = Guid.CreateVersion7() };
        facility.Update(name, location, capacity, type, @operator, zone);
        return facility;
    }

    public void Update(string name, GeoPoint location, int capacity, FacilityType type, string @operator, string zone)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (capacity <= 0)
        {
            throw new DomainValidationException(nameof(capacity), "must be greater than zero");
        }

        Name = Guard.Text(name, nameof(name), NameMaxLength);
        Location = location;
        Capacity = capacity;
        Type = Guard.Defined(type, nameof(type));
        Operator = Guard.Text(@operator, "operator", OperatorMaxLength);
        Zone = Guard.Text(zone, nameof(zone), ZoneMaxLength);
    }
}
