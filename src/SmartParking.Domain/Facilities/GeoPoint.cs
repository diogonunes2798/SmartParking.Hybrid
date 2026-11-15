using System.Globalization;
using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// WGS 84 point (EPSG:4326) locating a parking facility (ADR-0003).
/// The latitude range is the one accepted by Redis GEO (<c>GEOADD</c>), so every stored location
/// can later be loaded into the availability read model (UC-03) without being rejected.
/// </summary>
public sealed record GeoPoint
{
    /// <summary>Redis GEO limit (Web Mercator, EPSG:3857), stricter than WGS 84's ±90°.</summary>
    public const double MaxLatitude = 85.05112878;

    public const double MaxLongitude = 180;

    public GeoPoint(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || latitude < -MaxLatitude || latitude > MaxLatitude)
        {
            throw new DomainValidationException(
                "location.latitude", string.Create(CultureInfo.InvariantCulture, $"must be between -{MaxLatitude} and {MaxLatitude}"));
        }

        if (double.IsNaN(longitude) || longitude < -MaxLongitude || longitude > MaxLongitude)
        {
            throw new DomainValidationException(
                "location.longitude", string.Create(CultureInfo.InvariantCulture, $"must be between -{MaxLongitude} and {MaxLongitude}"));
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }
}
