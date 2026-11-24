using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Tests.Common;

internal static class HealthAssertions
{
    public const string Live = "/health/live";

    public const string Ready = "/health/ready";

    public static async Task<HealthReportResponse> ReadReportAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var report = await response.Content.ReadFromJsonAsync<HealthReportResponse>(JsonSerializerOptions.Web, cancellationToken);
        Assert.NotNull(report);
        return report;
    }

    public static async Task AssertReadyAsync(HttpClient client, string[] expectedChecks, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(Ready, UriKind.Relative), cancellationToken);
        var report = await ReadReportAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", report.Status);
        Assert.Equal(expectedChecks.Order(StringComparer.Ordinal), report.Checks.Keys.Order(StringComparer.Ordinal));
        Assert.All(report.Checks.Values, check => Assert.Equal("Healthy", check.Status));
    }

    public static async Task AssertLiveAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(Live, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
