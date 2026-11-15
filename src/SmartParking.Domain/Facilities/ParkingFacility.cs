using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>Parking facility (spec §3, Cap. 3.5). Configuration data, stored relationally (RF-06).</summary>
public sealed class ParkingFacility : IVersioned, IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

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

    /// <param name="occurredAt">When the change happened; stamps the <see cref="FacilityChanged"/> event (read model order).</param>
    public static ParkingFacility Create(
        string name, GeoPoint location, int capacity, FacilityType type, string @operator, string zone, DateTimeOffset occurredAt)
    {
        var facility = new ParkingFacility { Id = Guid.CreateVersion7() };
        facility.Update(name, location, capacity, type, @operator, zone, occurredAt);
        return facility;
    }

    public void Update(
        string name, GeoPoint location, int capacity, FacilityType type, string @operator, string zone, DateTimeOffset occurredAt)
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
        _domainEvents.Add(new FacilityChanged(
            Id, Name, Location.Latitude, Location.Longitude, Capacity, Type, Zone, occurredAt));
    }

    /// <summary>Records the deletion for the read model; the caller removes the entity in the same unit of work.</summary>
    public void MarkDeleted(DateTimeOffset occurredAt) => _domainEvents.Add(new FacilityDeleted(Id, occurredAt));

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        var events = _domainEvents.ToArray();
        _domainEvents.Clear();
        return events;
    }
}
