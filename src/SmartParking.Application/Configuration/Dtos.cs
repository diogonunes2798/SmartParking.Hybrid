using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Tariffs;

namespace SmartParking.Application.Configuration;

/// <summary>
/// GeoJSON Point (RFC 7946): <c>{"type":"Point","coordinates":[longitude, latitude]}</c>.
/// Longitude comes first, the same order as Redis <c>GEOADD</c> (ADR-0003).
/// </summary>
public sealed record GeoJsonPoint(string Type, IReadOnlyList<double> Coordinates)
{
    public const string PointType = "Point";

    public static GeoJsonPoint From(GeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return new(PointType, [point.Longitude, point.Latitude]);
    }

    public GeoPoint ToGeoPoint()
    {
        if (!string.Equals(Type, PointType, StringComparison.Ordinal))
        {
            throw new DomainValidationException("location.type", "must be \"Point\"");
        }

        if (Coordinates is not { Count: 2 })
        {
            throw new DomainValidationException("location.coordinates", "must be [longitude, latitude]");
        }

        return new GeoPoint(latitude: Coordinates[1], longitude: Coordinates[0]);
    }
}

/// <summary>Paging of list queries.</summary>
public sealed record PageRequest(int Skip = 0, int Take = PageRequest.DefaultTake)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public PageRequest Validated() =>
        Skip < 0 ? throw new DomainValidationException("skip", "must not be negative")
        : Take is < 1 or > MaxTake ? throw new DomainValidationException("take", $"must be between 1 and {MaxTake}")
        : this;
}

// ---- Facilities ----

public sealed record FacilityDto(
    Guid Id, string Name, GeoJsonPoint Location, int Capacity, FacilityType Type, string Operator, string Zone, uint Version)
{
    public static FacilityDto From(ParkingFacility f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new(f.Id, f.Name, GeoJsonPoint.From(f.Location), f.Capacity, f.Type, f.Operator, f.Zone, f.Version);
    }
}

public sealed record CreateFacility(
    string Name, GeoJsonPoint Location, int Capacity, FacilityType Type, string Operator, string Zone);

/// <summary><see cref="Version"/> is the value the client read (optimistic concurrency).</summary>
public sealed record UpdateFacility(
    string Name, GeoJsonPoint Location, int Capacity, FacilityType Type, string Operator, string Zone, uint Version);

// ---- Spots ----

public sealed record SpotDto(Guid Id, Guid FacilityId, string Code, VehicleType AllowedVehicleType, uint Version)
{
    public static SpotDto From(ParkingSpot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.FacilityId, s.Code, s.AllowedVehicleType, s.Version);
    }
}

public sealed record CreateSpot(string Code, VehicleType AllowedVehicleType);

public sealed record UpdateSpot(string Code, VehicleType AllowedVehicleType, uint Version);

// ---- Sensors ----

public sealed record SensorDto(Guid Id, Guid SpotId, string Model, string FirmwareVersion, SensorStatus Status, uint Version)
{
    public static SensorDto From(Sensor s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.SpotId, s.Model, s.FirmwareVersion, s.Status, s.Version);
    }
}

public sealed record CreateSensor(Guid SpotId, string Model, string FirmwareVersion, SensorStatus Status);

public sealed record UpdateSensor(Guid SpotId, string Model, string FirmwareVersion, SensorStatus Status, uint Version);

// ---- Tariffs ----

public sealed record TariffRulesDto(decimal HourlyRate, int FreeMinutes, decimal? DailyCap = null)
{
    public static TariffRulesDto From(TariffRules r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.HourlyRate, r.FreeMinutes, r.DailyCap);
    }

    public TariffRules ToDomain() => new(HourlyRate, FreeMinutes, DailyCap);
}

public sealed record TariffDto(
    Guid Id, string Zone, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, TariffRulesDto Rules, uint Version)
{
    public static TariffDto From(Tariff t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new(t.Id, t.Zone, t.Validity.ValidFrom, t.Validity.ValidTo, TariffRulesDto.From(t.Rules), t.Version);
    }
}

/// <summary><see cref="ValidTo"/> is optional (open-ended tariff); every other field is required.</summary>
public sealed record CreateTariff(string Zone, DateTimeOffset ValidFrom, TariffRulesDto Rules, DateTimeOffset? ValidTo = null);

public sealed record UpdateTariff(
    string Zone, DateTimeOffset ValidFrom, TariffRulesDto Rules, uint Version, DateTimeOffset? ValidTo = null);
