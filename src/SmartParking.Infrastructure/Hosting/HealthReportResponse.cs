namespace SmartParking.Infrastructure.Hosting;

/// <summary>Body of <c>/health/ready</c>.</summary>
public sealed record HealthReportResponse(
    string Status,
    double TotalDurationMs,
    IReadOnlyDictionary<string, HealthCheckResponse> Checks);
