using NetArchTest.Rules;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>
/// The <c>ProjectionRebuilder</c> only composes the ports of Application with the implementations of Infrastructure
/// (spec 004; plan F5a, proposal 7): the rebuild itself lives in <c>RebuildAvailability</c>, and the tool touches no
/// database library directly, so the rebuild that F12 measures is the same code the hosts run.
/// </summary>
public sealed class RebuilderIsolationTests
{
    private static readonly System.Reflection.Assembly RebuilderAssembly = typeof(SmartParking.ProjectionRebuilder.ExitCodes).Assembly;

    [Fact]
    public void RebuilderAssembly_Inspected_UsesNoDatabaseLibraryNorHostProject()
    {
        // Act
        var result = Types.InAssembly(RebuilderAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                SolutionAssemblies.ApiNamespace,
                SolutionAssemblies.WorkersNamespace,
                "Npgsql",
                "Microsoft.EntityFrameworkCore",
                "MongoDB",
                "StackExchange.Redis")
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
    }
}
