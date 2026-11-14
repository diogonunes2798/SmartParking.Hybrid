namespace SmartParking.Api.IntegrationTests;

/// <summary>Shape of the JSON written by ServiceDefaultsExtensions for /health/*.</summary>
internal sealed record HealthResponse(string Status, double TotalDurationMs, IReadOnlyList<HealthEntry> Checks);

internal sealed record HealthEntry(string Name, string Status, double DurationMs, string? Description);
