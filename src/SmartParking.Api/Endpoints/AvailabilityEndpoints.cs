using SmartParking.Application.Availability;
using SmartParking.Application.Configuration;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Endpoints;

/// <summary>
/// UC-03 — availability by proximity (RF-03), through IAvailabilityQueries. Hybrid: reads only the Redis read model (GEO +
/// hashes, ADR-0008), never PostgreSQL. Monolithic: one PostGIS query (ADR-0012). Target p95 &lt; 200 ms (RNF-01), Phase 9.
/// </summary>
internal static class AvailabilityEndpoints
{
    public static IEndpointRouteBuilder MapAvailabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/availability", async (
                double? lat, double? lon, double? radius, VehicleType? type, int? limit,
                IAvailabilityQueries queries, CancellationToken ct) =>
            {
                var search = AvailabilitySearch.Create(lat, lon, radius, type, limit);
                var facilities = await queries.SearchAsync(search, ct);
                return TypedResults.Ok(facilities.Select(FacilityAvailabilityDto.From).ToList());
            })
            .WithTags("Availability")
            .WithSummary("Facilities within a radius (metres) of a point, nearest first, with free spots (UC-03).")
            .ProducesValidationProblem();

        return app;
    }
}

/// <summary>Response item of UC-03. The location is GeoJSON (longitude first), as in RF-06.</summary>
internal sealed record FacilityAvailabilityDto(
    Guid FacilityId,
    string Name,
    string Zone,
    FacilityType Type,
    GeoJsonPoint Location,
    int Capacity,
    double DistanceMeters,
    int Spots,
    int Free)
{
    public static FacilityAvailabilityDto From(FacilityAvailability a) => new(
        a.FacilityId,
        a.Name,
        a.Zone,
        a.Type,
        GeoJsonPoint.From(new GeoPoint(a.Latitude, a.Longitude)),
        a.Capacity,
        Math.Round(a.DistanceMeters, 1),
        a.Spots,
        a.Free);
}
