using NetArchTest.Rules;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>
/// The simulator is a black-box client of the Api (spec 003, AC-19): it registers the facility and sends events only
/// through HTTP, so it references no project of <c>src/</c> and no database library (no SQL on the side).
/// </summary>
public sealed class SimulatorIsolationTests
{
    private static readonly System.Reflection.Assembly SimulatorAssembly = typeof(SmartParking.SensorSimulator.ExitCodes).Assembly;

    [Fact]
    public void SimulatorAssembly_Inspected_UsesNoSolutionProjectNorDatabaseLibrary_AC19()
    {
        // Act
        var result = Types.InAssembly(SimulatorAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                SolutionAssemblies.DomainNamespace,
                SolutionAssemblies.ApplicationNamespace,
                SolutionAssemblies.InfrastructureNamespace,
                SolutionAssemblies.ApiNamespace,
                SolutionAssemblies.WorkersNamespace,
                "Npgsql",
                "Microsoft.EntityFrameworkCore",
                "MongoDB",
                "StackExchange.Redis")
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
        Assert.DoesNotContain(
            SimulatorAssembly.GetReferencedAssemblies(),
            reference => reference.Name is { } name
                && (name.StartsWith("SmartParking.", StringComparison.Ordinal)
                    || name.StartsWith("Npgsql", StringComparison.Ordinal)
                    || name.StartsWith("MongoDB.", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    || name == "StackExchange.Redis"));
    }
}
