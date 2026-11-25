using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Configuration;

/// <summary>
/// AC-23 (documental, checked here as well): <c>tests/http/configuration.http</c> has a request for every endpoint
/// under <c>/api</c>, and no request to a route that does not exist.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed partial class ConfigurationHttpFileTests(ApiFixture fixture)
{
    [Fact]
    public void HttpFile_EveryConfigurationEndpoint_HasOneRequest_AC23()
    {
        // Arrange
        var endpoints = fixture.Hybrid.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {Normalise(endpoint.RoutePattern.RawText!)}"))
            .ToHashSet(StringComparer.Ordinal);

        // Act
        var requests = File.ReadLines(FindHttpFile())
            .Select(line => RequestLine().Match(line))
            .Where(match => match.Success)
            .Select(match => $"{match.Groups["method"].Value} {Normalise(match.Groups["path"].Value)}")
            .ToHashSet(StringComparer.Ordinal);

        // Assert
        Assert.NotEmpty(endpoints);
        Assert.Empty(endpoints.Except(requests));
        Assert.Empty(requests.Except(endpoints));
    }

    /// <summary>Route parameters and REST Client variables both become <c>{}</c>; no query string, no trailing slash.</summary>
    private static string Normalise(string path)
    {
        var withoutQuery = path.Split('?')[0];
        return Parameter().Replace(withoutQuery, "{}").TrimEnd('/');
    }

    private static string FindHttpFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "http", "configuration.http");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("tests/http/configuration.http was not found above the test output folder.");
    }

    [GeneratedRegex(@"^(?<method>GET|POST|PUT|DELETE|PATCH) \{\{baseUrl\}\}(?<path>/api/\S*)")]
    private static partial Regex RequestLine();

    [GeneratedRegex(@"\{\{[^}]+\}\}|\{[^}]+\}")]
    private static partial Regex Parameter();
}
