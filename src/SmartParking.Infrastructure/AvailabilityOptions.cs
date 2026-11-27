namespace SmartParking.Infrastructure;

/// <summary>
/// Binds the <c>Availability</c> section (spec 004). <see cref="RetryAfterSeconds"/> is the <c>Retry-After</c> of the
/// queries' 503 when Redis is unreachable; kept apart from the ingestion's so each can be tuned on its own.
/// </summary>
public sealed class AvailabilityOptions
{
    public const string SectionName = "Availability";

    public static readonly string ValidationMessage = $"{SectionName}:{nameof(RetryAfterSeconds)} must be greater than 0.";

    public int RetryAfterSeconds { get; set; } = 1;

    public bool IsValid() => RetryAfterSeconds > 0;
}
