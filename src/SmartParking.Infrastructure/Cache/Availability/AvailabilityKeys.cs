using System.Globalization;

namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>
/// Keys and field names of the availability view (spec 004, «Estruturas Redis»). Every key starts with
/// <see cref="Prefix"/>, so the rebuild can remove the view without touching anything else (AC-11). Identifiers are
/// written as lowercase UUID text, the same text the projector compares for the tie-break.
/// </summary>
internal static class AvailabilityKeys
{
    public const string Prefix = "avail:";
    public const string SpotPrefix = Prefix + "spot:";
    public const string FacilityPrefix = Prefix + "facility:";
    public const string ZonePrefix = Prefix + "zone:";
    public const string OperatorPrefix = Prefix + "operator:";
    public const string Geo = Prefix + "geo:facilities";

    public const string ChangedChannel = "availability:changed";

    public const string FacilityIdField = "facilityId";
    public const string ZoneIdField = "zoneId";
    public const string TypeField = "type";
    public const string StateField = "state";
    public const string LastTimestampField = "lastTimestamp";
    public const string LastEventIdField = "lastEventId";
    public const string NameField = "name";
    public const string OperatorIdField = "operatorId";
    public const string LatitudeField = "lat";
    public const string LongitudeField = "lon";
    public const string FreeField = "free";
    public const string TotalField = "total";

    public const string Free = "Free";
    public const string Occupied = "Occupied";

    public static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    public static string Spot(Guid spotId) => SpotPrefix + Id(spotId);

    public static string Facility(Guid facilityId) => FacilityPrefix + Id(facilityId);

    public static string FacilityZones(Guid facilityId) => Facility(facilityId) + ":zones";

    public static string Zone(Guid zoneId) => ZonePrefix + Id(zoneId);

    public static string OperatorFacilities(Guid operatorId) => OperatorPrefix + Id(operatorId) + ":facilities";

    public static string FreeOf(string spotType) => FreeField + ":" + spotType;

    public static string TotalOf(string spotType) => TotalField + ":" + spotType;
}
