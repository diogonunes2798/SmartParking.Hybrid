using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Zone of a parking facility (for example a floor). The facility never changes; the name is unique in the facility.
/// The database refuses to delete a zone while spots or sensors point at it (FK RESTRICT, AC-13).
/// </summary>
public sealed class Zone : AggregateRoot
{
    public const int NameMaxLength = 100;

    private Zone()
    {
    }

    public Guid FacilityId { get; private init; }

    public string Name { get; private set; } = string.Empty;

    public uint Version { get; private set; }

    public static Zone Create(Guid id, Guid facilityId, string name)
    {
        var zone = new Zone
        {
            Id = Guard.Id(id),
            FacilityId = Guard.Id(facilityId),
            Name = Guard.Text(name, NameMaxLength),
        };

        zone.Raise(new ZoneCreated(zone.Id, zone.FacilityId, zone.Name));
        return zone;
    }

    /// <summary>Replaces the name (PUT). Returns <c>false</c>, without raising an event, when nothing changed.</summary>
    public bool Rename(string name)
    {
        var newName = Guard.Text(name, NameMaxLength);
        if (newName == Name)
        {
            return false;
        }

        Name = newName;
        Raise(new ZoneUpdated(Id, FacilityId, Name));
        return true;
    }

    public void MarkForDeletion() => Raise(new ZoneDeleted(Id, FacilityId, Name));
}
