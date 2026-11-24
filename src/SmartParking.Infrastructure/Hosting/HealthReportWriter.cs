using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartParking.Infrastructure.Hosting;

/// <summary>
/// Writes the readiness report as JSON: overall status and, per check, status and duration.
/// Exception messages are left out on purpose (they can contain host names and credentials).
/// </summary>
internal static class HealthReportWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new HealthReportResponse(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new HealthCheckResponse(entry.Value.Status.ToString(), entry.Value.Duration.TotalMilliseconds)));

        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
