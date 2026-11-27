using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Features.Availability;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Domain.Occupancy;
using SmartParking.ProjectionRebuilder;
using static SmartParking.Api.Tests.Fixtures.AvailabilityView;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>The availability queries for the tests (spec 004, F5b): requests, typed answers, the rebuild and the projector.</summary>
internal static class AvailabilityApi
{
    public const string Route = "/api/availability";
    public const string NearbyRoute = Route + "/nearby";

    /// <summary>Mean Earth radius of the Redis GEO commands (<c>geohash_helper.c</c>), in metres.</summary>
    public const double RedisEarthRadiusMeters = 6372797.560856;

    public static readonly DateTimeOffset T = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string query, CancellationToken cancellationToken) =>
        client.GetAsync(new Uri($"{Route}?{query}", UriKind.Relative), cancellationToken);

    public static Task<HttpResponseMessage> NearbyAsync(HttpClient client, string query, CancellationToken cancellationToken) =>
        client.GetAsync(new Uri($"{NearbyRoute}?{query}", UriKind.Relative), cancellationToken);

    public static async Task<AvailabilityResponse> GetOkAsync(HttpClient client, string query, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(client, query, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AvailabilityResponse>(cancellationToken))!;
    }

    public static async Task<NearbyResponse> NearbyOkAsync(HttpClient client, string query, CancellationToken cancellationToken)
    {
        using var response = await NearbyAsync(client, query, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NearbyResponse>(cancellationToken))!;
    }

    /// <summary>A ProblemDetails answer with this status; returns its JSON body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>The <c>ProjectionRebuilder</c>, run in process against an isolated environment (spec 004, ronda 6).</summary>
    public static Task<int> RunRebuilderAsync(Isolated environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return RebuilderCommand.Create(environment.Rebuilder).Parse([]).InvokeAsync(cancellationToken: cancellationToken);
    }

    /// <summary>Applies an <c>Occupied</c> at <see cref="T"/> to the spot through the host's projector.</summary>
    public static Task<ProjectionOutcome> OccupyAsync(IServiceProvider services, Site site, SpotWithSensor spot, CancellationToken cancellationToken) =>
        services.GetRequiredService<IOccupancyProjector>().ApplyAsync(Occupancy(site, spot, OccupancyEventType.Occupied, T), publishChanges: false, cancellationToken);

    /// <summary>Great-circle distance with the formula and radius Redis uses, to compare with <c>distanceMeters</c>.</summary>
    public static double HaversineMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180;

        var u = Math.Sin((Radians(latitude2) - Radians(latitude1)) / 2);
        var v = Math.Sin((Radians(longitude2) - Radians(longitude1)) / 2);
        return 2 * RedisEarthRadiusMeters * Math.Asin(Math.Sqrt((u * u) + (Math.Cos(Radians(latitude1)) * Math.Cos(Radians(latitude2)) * v * v)));
    }

    public static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
