using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using SmartParking.Infrastructure.Relational.Monolithic;

namespace SmartParking.Api.IntegrationTests;

/// <summary>
/// Persistence variant under test (Phase 8, ADR-0012), from the environment variable <c>Persistence__Mode</c>, as the
/// hosts read it:
/// <code>
/// Persistence__Mode=Hybrid dotnet test        # default
/// Persistence__Mode=Monolithic dotnet test
/// </code>
/// The functional tests (HTTP contract, RF-06, UC-02, UC-03, UC-04) run in both. Tests of the internals of one variant
/// (Redis keys, streams, MongoDB documents and plans) carry <see cref="HybridFactAttribute"/> and are skipped in the other.
/// </summary>
public static class TestMode
{
    public static PersistenceMode Current { get; } = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PersistenceModeConfiguration.Key] = Environment.GetEnvironmentVariable("Persistence__Mode"),
        })
        .Build()
        .GetPersistenceMode();

    public static bool IsMonolithic => Current == PersistenceMode.Monolithic;

    public static bool IsHybrid => Current == PersistenceMode.Hybrid;

    public const string HybridOnly = "Internals of the hybrid variant (Redis, MongoDB): skipped with Persistence__Mode=Monolithic.";
}

/// <summary>A fact about the hybrid variant's internals; skipped in the monolithic run.</summary>
public sealed class HybridFactAttribute : FactAttribute
{
    public HybridFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (TestMode.IsMonolithic)
        {
            Skip = TestMode.HybridOnly;
        }
    }
}

/// <summary>A theory about the hybrid variant's internals; skipped in the monolithic run.</summary>
public sealed class HybridTheoryAttribute : TheoryAttribute
{
    public HybridTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (TestMode.IsMonolithic)
        {
            Skip = TestMode.HybridOnly;
        }
    }
}
