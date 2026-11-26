namespace SmartParking.Application.Features.Ingestion;

/// <summary>
/// Ingestion rules built from the <c>Ingestion</c> configuration (spec 003). <see cref="FutureTolerance"/> is how far
/// ahead of the server clock a device timestamp may be (rule 8); older events are accepted without limit.
/// </summary>
public sealed record IngestionPolicy
{
    public IngestionPolicy(TimeSpan futureTolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(futureTolerance, TimeSpan.Zero);
        FutureTolerance = futureTolerance;
    }

    public TimeSpan FutureTolerance { get; }
}
