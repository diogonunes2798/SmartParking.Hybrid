namespace SmartParking.Infrastructure;

/// <summary>
/// Binds the <c>Persistence</c> section. <see cref="Mode"/> is kept as text so that a missing or
/// unknown value surfaces as an options validation error naming the key (spec 000, AC-9),
/// instead of a binder conversion error.
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    public const string ModeKey = SectionName + ":Mode";

    public static readonly string ValidationMessage =
        $"{ModeKey} must be '{nameof(PersistenceMode.Hybrid)}' or '{nameof(PersistenceMode.Monolithic)}'.";

    public string? Mode { get; set; }

    /// <summary>Exact, case-sensitive match; numeric values are rejected.</summary>
    public static bool TryParseMode(string? value, out PersistenceMode mode)
    {
        (var valid, mode) = value switch
        {
            nameof(PersistenceMode.Hybrid) => (true, PersistenceMode.Hybrid),
            nameof(PersistenceMode.Monolithic) => (true, PersistenceMode.Monolithic),
            _ => (false, default(PersistenceMode)),
        };

        return valid;
    }
}
