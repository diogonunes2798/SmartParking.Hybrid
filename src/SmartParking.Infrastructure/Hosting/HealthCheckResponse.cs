namespace SmartParking.Infrastructure.Hosting;

/// <summary>One readiness check (<c>postgres</c>, <c>mongo</c> or <c>redis</c>).</summary>
public sealed record HealthCheckResponse(string Status, double DurationMs);
