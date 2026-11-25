using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.HttpFiles;

/// <summary>
/// The <c>tests/http/*.http</c> files have one request for every endpoint under <c>/api</c> (spec 001 AC-23 for
/// <c>configuration.http</c>; spec 002 AC-20 for <c>sessions.http</c>). Each file covers its own route prefixes,
/// with no request to a route that does not exist; together they cover every <c>/api</c> route (plan 002a, proposal 12).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed partial class HttpFileTests(ApiFixture fixture)
{
    /// <summary>Each file and the route prefixes it owns.</summary>
    private static readonly Dictionary<string, string[]> Files = new(StringComparer.Ordinal)
    {
        ["configuration.http"] = ["/api/facilities", "/api/sensors"],
        ["sessions.http"] = ["/api/users", "/api/sessions"],
    };

    [Fact]
    public void HttpFile_EveryConfigurationEndpoint_HasOneRequest_AC23() => AssertFileCoversItsEndpoints("configuration.http");

    [Fact]
    public void HttpFile_EverySessionEndpoint_HasOneRequest_AC20() => AssertFileCoversItsEndpoints("sessions.http");

    [Fact]
    public void HttpFiles_Together_CoverEveryApiRoute()
    {
        // Arrange
        var endpoints = ApiEndpoints();

        // Act
        var uncovered = endpoints.Where(endpoint => !Files.Values.SelectMany(prefixes => prefixes).Any(prefix => Owns(prefix, endpoint)));

        // Assert
        Assert.Empty(uncovered);
    }

    private void AssertFileCoversItsEndpoints(string file)
    {
        // Arrange
        var prefixes = Files[file];
        var endpoints = ApiEndpoints().Where(endpoint => prefixes.Any(prefix => Owns(prefix, endpoint))).ToHashSet(StringComparer.Ordinal);

        // Act
        var requests = File.ReadLines(FindHttpFile(file))
            .Select(line => RequestLine().Match(line))
            .Where(match => match.Success)
            .Select(match => $"{match.Groups["method"].Value} {Normalise(match.Groups["path"].Value)}")
            .Where(request => prefixes.Any(prefix => Owns(prefix, request)))
            .ToHashSet(StringComparer.Ordinal);

        // Assert
        Assert.NotEmpty(endpoints);
        Assert.Empty(endpoints.Except(requests));
        Assert.Empty(requests.Except(endpoints));
    }

    /// <summary>Every <c>METHOD /api/route</c> of the running Api, with route parameters as <c>{}</c>.</summary>
    private HashSet<string> ApiEndpoints() =>
        fixture.Hybrid.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {Normalise(endpoint.RoutePattern.RawText!)}"))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary><c>"POST /api/users/{}"</c> belongs to the prefix <c>/api/users</c>; <c>/api/usersX</c> does not.</summary>
    private static bool Owns(string prefix, string request)
    {
        var path = request[(request.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        return path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    /// <summary>Route parameters and REST Client variables both become <c>{}</c>; no query string, no trailing slash.</summary>
    private static string Normalise(string path)
    {
        var withoutQuery = path.Split('?')[0];
        return Parameter().Replace(withoutQuery, "{}").TrimEnd('/');
    }

    private static string FindHttpFile(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "http", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"tests/http/{file} was not found above the test output folder.");
    }

    [GeneratedRegex(@"^(?<method>GET|POST|PUT|DELETE|PATCH) \{\{baseUrl\}\}(?<path>/api/\S*)")]
    private static partial Regex RequestLine();

    [GeneratedRegex(@"\{\{[^}]+\}\}|\{[^}]+\}")]
    private static partial Regex Parameter();
}
