using System.Globalization;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Availability;

/// <summary>
/// Parameters of <c>GET /api/availability?lat=&amp;lon=&amp;radius=&amp;type=</c> (UC-03). Validated here so that the
/// read model never sees a point Redis GEO would reject or a radius that would scan the whole city set.
/// </summary>
public sealed record AvailabilitySearch(
    double Latitude, double Longitude, double RadiusMeters, VehicleType? VehicleType, int Limit)
{
    public const double DefaultRadiusMeters = 1_000;

    /// <summary>
    /// Upper bound of the radius. Provisional (50 km covers a municipality such as Coimbra); a larger radius would
    /// turn a proximity query into a scan of every facility. TODO(Diogo): confirm with the scenario of Cap. 3.
    /// </summary>
    public const double MaxRadiusMeters = 50_000;

    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>Builds and validates a search from the raw query values (null = not given).</summary>
    public static AvailabilitySearch Create(double? latitude, double? longitude, double? radiusMeters, VehicleType? vehicleType, int? limit)
    {
        if (latitude is null)
        {
            throw new DomainValidationException("lat", "is required");
        }

        if (longitude is null)
        {
            throw new DomainValidationException("lon", "is required");
        }

        // GeoPoint applies the Redis GEO limits (ADR-0003); rethrow with the query parameter names.
        try
        {
            _ = new GeoPoint(latitude.Value, longitude.Value);
        }
        catch (DomainValidationException ex)
        {
            var field = ex.Field.EndsWith("latitude", StringComparison.Ordinal) ? "lat" : "lon";
            throw new DomainValidationException(field, "is out of range (latitude ±85.05112878, longitude ±180)");
        }

        var radius = radiusMeters ?? DefaultRadiusMeters;
        if (double.IsNaN(radius) || radius <= 0 || radius > MaxRadiusMeters)
        {
            throw new DomainValidationException(
                "radius", string.Create(CultureInfo.InvariantCulture, $"must be greater than 0 and at most {MaxRadiusMeters} metres"));
        }

        if (vehicleType is { } type && !Enum.IsDefined(type))
        {
            throw new DomainValidationException("type", "is not a known vehicle type");
        }

        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            throw new DomainValidationException("limit", $"must be between 1 and {MaxLimit}");
        }

        return new AvailabilitySearch(latitude.Value, longitude.Value, radius, vehicleType, take);
    }
}
