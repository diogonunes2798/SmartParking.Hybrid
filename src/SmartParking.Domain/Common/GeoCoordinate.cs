namespace SmartParking.Domain.Common;

/// <summary>WGS 84 point (SRID 4326) of a parking facility (RF-04).</summary>
public sealed record GeoCoordinate
{
    /// <summary>
    /// Latitude limits of the Web Mercator projection (EPSG:3857), the range the Redis GEO index accepts. Facilities
    /// beyond it could not enter the availability view, so the rebuild would fail on every run (review of F5a,
    /// finding 1; spec 001, revised 2026-11-27). No parking facility lies there.
    /// </summary>
    public const double MinLatitude = -85.05112878;

    public const double MaxLatitude = 85.05112878;

    public const double MinLongitude = -180;

    public const double MaxLongitude = 180;

    public GeoCoordinate(double latitude, double longitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latitude, MinLatitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(latitude, MaxLatitude);
        ArgumentOutOfRangeException.ThrowIfLessThan(longitude, MinLongitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(longitude, MaxLongitude);

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    public static bool IsValidLatitude(double latitude) =>
        latitude is >= MinLatitude and <= MaxLatitude;

    public static bool IsValidLongitude(double longitude) =>
        longitude is >= MinLongitude and <= MaxLongitude;
}
