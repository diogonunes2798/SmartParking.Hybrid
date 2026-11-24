using NetArchTest.Rules;
using static SmartParking.ArchitectureTests.SolutionAssemblies;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>Dependency direction of Clean Architecture (spec 000, AC-1 and AC-3).</summary>
public sealed class LayerDependencyTests
{
    [Fact]
    public void DomainAssembly_Inspected_DependsOnNoOtherSolutionProject_AC1()
    {
        // Act
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, ApiNamespace, WorkersNamespace)
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
        Assert.Empty(ReferencedSolutionAssemblies(DomainAssembly));
    }

    [Fact]
    public void ApplicationAssembly_Inspected_DependsOnlyOnDomain_AC1()
    {
        // Act
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, ApiNamespace, WorkersNamespace)
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
        Assert.All(ReferencedSolutionAssemblies(ApplicationAssembly), name => Assert.Equal(DomainNamespace, name));
    }

    [Fact]
    public void ApplicationAssembly_Inspected_UsesNoPersistenceLibrary_AC3()
    {
        // Act
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "MongoDB.Driver", "MongoDB.Bson", "StackExchange.Redis", "Npgsql")
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
        Assert.DoesNotContain(
            ApplicationAssembly.GetReferencedAssemblies(),
            reference => reference.Name is "Microsoft.EntityFrameworkCore" or "MongoDB.Driver" or "StackExchange.Redis");
    }

    private static string[] ReferencedSolutionAssemblies(System.Reflection.Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("SmartParking.", StringComparison.Ordinal))];
}
