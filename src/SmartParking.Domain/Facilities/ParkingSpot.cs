using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Parking spot (spec §3). Its current state (free/occupied) is deliberately absent:
/// it lives only in the Redis read model (UC-03), never in the relational store.
/// </summary>
public sealed class ParkingSpot : IVersioned, IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public const int CodeMaxLength = 50;

    private ParkingSpot()
    {
        Code = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid FacilityId { get; private set; }

    /// <summary>Human-readable code, unique within the facility (e.g. "P1-042").</summary>
    public string Code { get; private set; }

    public VehicleType AllowedVehicleType { get; private set; }

    public uint Version { get; private set; }

    /// <param name="occurredAt">When the change happened; stamps the <see cref="SpotChanged"/> event (read model order).</param>
    public static ParkingSpot Create(Guid facilityId, string code, VehicleType allowedVehicleType, DateTimeOffset occurredAt)
    {
        var spot = new ParkingSpot
        {
            Id = Guid.CreateVersion7(),
            FacilityId = Guard.Id(facilityId, nameof(facilityId)),
        };
        spot.Update(code, allowedVehicleType, occurredAt);
        return spot;
    }

    public void Update(string code, VehicleType allowedVehicleType, DateTimeOffset occurredAt)
    {
        Code = Guard.Text(code, nameof(code), CodeMaxLength);
        AllowedVehicleType = Guard.Defined(allowedVehicleType, nameof(allowedVehicleType));
        _domainEvents.Add(new SpotChanged(Id, FacilityId, AllowedVehicleType, occurredAt));
    }

    /// <summary>Records the deletion for the read model; the caller removes the entity in the same unit of work.</summary>
    public void MarkDeleted(DateTimeOffset occurredAt) => _domainEvents.Add(new SpotDeleted(Id, FacilityId, occurredAt));

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        var events = _domainEvents.ToArray();
        _domainEvents.Clear();
        return events;
    }
}
