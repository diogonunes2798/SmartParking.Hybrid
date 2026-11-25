using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Parking facility with its weekly opening hours (RF-06). The operator never changes.
/// <see cref="Version"/> is the PostgreSQL <c>xmin</c>, used as the ETag (spec 001).
/// </summary>
public sealed class ParkingFacility : AggregateRoot
{
    public const int NameMaxLength = 200;

    public const int AddressMaxLength = 500;

    public const int TimeZoneMaxLength = 64;

    private readonly List<FacilityOpeningHours> openingHours = [];

    private ParkingFacility()
    {
    }

    public Guid OperatorId { get; private init; }

    public string Name { get; private set; } = string.Empty;

    public string Address { get; private set; } = string.Empty;

    public GeoCoordinate Location { get; private set; } = null!;

    /// <summary>IANA identifier, for example <c>Europe/Lisbon</c>.</summary>
    public string TimeZone { get; private set; } = string.Empty;

    public FacilityStatus Status { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyList<FacilityOpeningHours> OpeningHours => openingHours.AsReadOnly();

    public static ParkingFacility Create(
        Guid id,
        Guid operatorId,
        string name,
        string address,
        GeoCoordinate location,
        string timeZone,
        IEnumerable<FacilityOpeningHours> openingHours)
    {
        var facility = new ParkingFacility
        {
            Id = Guard.Id(id),
            OperatorId = Guard.Id(operatorId),
            Status = FacilityStatus.Active,
        };

        facility.SetDetails(name, address, location, timeZone);
        facility.ReplaceOpeningHours(openingHours);
        facility.Raise(new FacilityCreated(
            facility.Id, facility.OperatorId, facility.Name, location.Latitude, location.Longitude, facility.TimeZone, facility.Status));

        return facility;
    }

    /// <summary>Replaces every editable field (PUT). Returns <c>false</c>, without raising an event, when nothing changed.</summary>
    public bool Update(
        string name,
        string address,
        GeoCoordinate location,
        string timeZone,
        FacilityStatus status,
        IEnumerable<FacilityOpeningHours> openingHours)
    {
        ArgumentNullException.ThrowIfNull(openingHours);

        FacilityOpeningHours[] requestedHours = [.. openingHours];
        var before = (Name, Address, Location, TimeZone, Status);

        SetDetails(name, address, location, timeZone);
        Status = Guard.Defined(status);
        var hoursChanged = ReplaceOpeningHours(requestedHours);

        if (!hoursChanged && before == (Name, Address, Location, TimeZone, Status))
        {
            return false;
        }

        Raise(new FacilityUpdated(Id, OperatorId, Name, Location.Latitude, Location.Longitude, TimeZone, Status));
        return true;
    }

    /// <summary>Records the deletion. The database refuses it (FK RESTRICT) while zones, spots, sensors or tariffs exist.</summary>
    public void MarkForDeletion() =>
        Raise(new FacilityDeleted(Id, OperatorId, Name, Location.Latitude, Location.Longitude, TimeZone, Status));

    private void SetDetails(string name, string address, GeoCoordinate location, string timeZone)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!IanaTimeZone.IsValid(timeZone))
        {
            throw new ArgumentException("The time zone must be an IANA identifier.", nameof(timeZone));
        }

        Name = Guard.Text(name, NameMaxLength);
        Address = Guard.Text(address, AddressMaxLength);
        Location = location;
        TimeZone = timeZone;
    }

    /// <summary>
    /// Merges the requested week into the current one by day: existing days are updated in place,
    /// missing days are removed and new days are added. Returns whether anything changed.
    /// </summary>
    private bool ReplaceOpeningHours(IEnumerable<FacilityOpeningHours> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var byDay = new Dictionary<DayOfWeek, FacilityOpeningHours>();
        foreach (var hours in requested)
        {
            ArgumentNullException.ThrowIfNull(hours);

            if (!byDay.TryAdd(hours.DayOfWeek, hours))
            {
                throw new ArgumentException($"{hours.DayOfWeek} appears more than once.", nameof(requested));
            }
        }

        var changed = openingHours.RemoveAll(current => !byDay.ContainsKey(current.DayOfWeek)) > 0;

        foreach (var (day, hours) in byDay)
        {
            var current = openingHours.Find(existing => existing.DayOfWeek == day);
            if (current is null)
            {
                openingHours.Add(hours);
                changed = true;
            }
            else if (!current.HasSameInterval(hours))
            {
                current.SetInterval(hours.OpensAt, hours.ClosesAt);
                changed = true;
            }
        }

        return changed;
    }
}
