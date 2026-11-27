namespace SmartParking.ProjectionRebuilder;

/// <summary>Exit codes of the rebuilder. Parse errors are 1 (System.CommandLine).</summary>
internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary><c>Persistence:Mode</c> is not <c>Hybrid</c>: the monolithic mode has no Redis view (F9).</summary>
    public const int NotHybrid = 3;

    /// <summary>A store failed during the rebuild; the view may be partial. The rebuild is idempotent: run it again.</summary>
    public const int RebuildFailed = 4;

    public const int Cancelled = 130;
}
