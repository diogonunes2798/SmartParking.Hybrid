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

    private const long TicksPerMicrosecond = 10;

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

    /// <summary>
    /// Whether <paramref name="instant"/> falls inside the opening hours of its local day, in the facility's time zone:
    /// [opensAt, closesAt), so the facility is already closed at closesAt (decision 4 of plan 002a). A day without a
    /// row is closed (spec 002).
    /// </summary>
    public bool IsOpenAt(DateTimeOffset instant) =>
        IsOpenAtLocal(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(TimeZone)));

    /// <summary>
    /// Whether [<paramref name="startsAt"/>, <paramref name="endsAt"/>) fits in a single opening interval of one local
    /// day (spec 002, reservation rules). The interval is half-open and instants are normalised to the microsecond,
    /// so its last instant is <c>endsAt − 1 µs</c> and a reservation may end exactly at closing time. Each day has at
    /// most one [opensAt, closesAt) with opensAt &lt; closesAt, so both ends open on the same local date means the
    /// whole interval fits.
    /// </summary>
    public bool FitsInOneOpeningInterval(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        if (endsAt <= startsAt)
        {
            return false;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        var first = TimeZoneInfo.ConvertTime(startsAt, zone);
        var last = TimeZoneInfo.ConvertTime(endsAt.AddTicks(-TicksPerMicrosecond), zone);

        return DateOnly.FromDateTime(first.DateTime) == DateOnly.FromDateTime(last.DateTime)
            && IsOpenAtLocal(first)
            && IsOpenAtLocal(last);
    }

    /// <summary>Records the deletion. The database refuses it (FK RESTRICT) while zones, spots, sensors or tariffs exist.</summary>
    public void MarkForDeletion() =>
        Raise(new FacilityDeleted(Id, OperatorId, Name, Location.Latitude, Location.Longitude, TimeZone, Status));

    private bool IsOpenAtLocal(DateTimeOffset local)
    {
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);

        return openingHours.Find(hours => hours.DayOfWeek == local.DayOfWeek) is { } day
            && time >= day.OpensAt
            && time < day.ClosesAt;
    }

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
