using SmartParking.Application.Features.Ingestion;

namespace SmartParking.Infrastructure;

/// <summary>
/// Binds the <c>Ingestion</c> section (spec 003). <see cref="FutureTolerance"/> is rule 8 (default 5 min).
/// <see cref="RetryAfterSeconds"/> is the <c>Retry-After</c> of a 503 when a store is unreachable.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    public static readonly string ValidationMessage =
        $"{SectionName}:{nameof(FutureTolerance)} must be 0 or greater and {SectionName}:{nameof(RetryAfterSeconds)} greater than 0.";

    public TimeSpan FutureTolerance { get; set; } = TimeSpan.FromMinutes(5);

    public int RetryAfterSeconds { get; set; } = 1;

    public bool IsValid() => FutureTolerance >= TimeSpan.Zero && RetryAfterSeconds > 0;

    public IngestionPolicy ToPolicy() => new(FutureTolerance);
}
