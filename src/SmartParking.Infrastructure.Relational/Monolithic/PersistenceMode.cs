using Microsoft.Extensions.Configuration;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// Which persistence architecture a host runs (spec §7, Cap. 6.5, ADR-0012). The Domain, the Application and the HTTP
/// contract are the same in both; only the infrastructure registered by the hosts changes.
/// </summary>
public enum PersistenceMode
{
    /// <summary>The proposed architecture: PostgreSQL + MongoDB (event log) + Redis (bus and read model).</summary>
    Hybrid = 1,

    /// <summary>The baseline for Cap. 6.5: everything in PostgreSQL (partitioned event table, availability by query).</summary>
    Monolithic = 2,
}

/// <summary>Reads <c>Persistence:Mode</c> (environment variable <c>Persistence__Mode</c>).</summary>
public static class PersistenceModeConfiguration
{
    public const string Key = "Persistence:Mode";

    /// <summary>
    /// The configured mode; <see cref="PersistenceMode.Hybrid"/> when the key is absent. An unknown value fails at
    /// startup: a typo must never run the wrong variant silently and spoil a comparison run.
    /// </summary>
    public static PersistenceMode GetPersistenceMode(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[Key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return PersistenceMode.Hybrid;
        }

        return Enum.TryParse<PersistenceMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            && !int.TryParse(value, out _)
            ? mode
            : throw new InvalidOperationException(
                $"Unknown {Key} '{value}'. Use '{PersistenceMode.Hybrid}' or '{PersistenceMode.Monolithic}'.");
    }
}
