namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>
/// Optimistic concurrency over the PostgreSQL <c>xmin</c> (spec 001): a change needs the version the client
/// read (<c>If-Match</c>). A missing version is 428; a different one is 412. A change committed between this check
/// and the save is caught by the unit of work, which also answers 412.
/// </summary>
public static class ConcurrencyCheck
{
    public static Error VersionRequired { get; } = Error.PreconditionRequired(
        "Concurrency.VersionRequired",
        "Send the ETag of the resource in the If-Match header.");

    public static Error StaleVersion { get; } = Error.PreconditionFailed(
        "Concurrency.StaleVersion",
        "The resource was changed since it was read. Read it again and retry with the new ETag.");

    /// <summary>The error for this pair of versions, or <c>null</c> when the client read the current one.</summary>
    public static Error? Verify(uint? expectedVersion, uint currentVersion) => expectedVersion switch
    {
        null => VersionRequired,
        { } expected when expected != currentVersion => StaleVersion,
        _ => null,
    };
}
