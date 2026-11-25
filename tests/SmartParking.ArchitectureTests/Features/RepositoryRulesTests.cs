using System.Reflection;
using static SmartParking.ArchitectureTests.SolutionAssemblies;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>Repository per aggregate root on the write side, never generic (D10, ADR 0003; spec 001, AC-21).</summary>
public sealed class RepositoryRulesTests
{
    private static readonly Assembly[] Assemblies = [DomainAssembly, ApplicationAssembly, InfrastructureAssembly];

    [Fact]
    public void Assemblies_Inspected_NoGenericRepository_AC21()
    {
        // Act
        var generic = Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsGenericTypeDefinition && type.Name.Contains("Repository", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .ToList();

        // Assert
        Assert.Empty(generic);
    }

    [Fact]
    public void RepositoryInterfaces_Inspected_LiveInAbstractionsWithoutIQueryableOrSave_AC21()
    {
        // Arrange
        var repositories = Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsInterface && type.Name.EndsWith("Repository", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(repositories);

        // Act
        var outsideAbstractions = repositories
            .Where(type => type.Assembly != ApplicationAssembly
                || type.Namespace?.StartsWith(ApplicationNamespace + ".Abstractions", StringComparison.Ordinal) != true)
            .Select(type => type.FullName);
        var queryableMembers = repositories
            .SelectMany(type => type.GetMethods())
            .Where(method => Mentions(method.ReturnType, typeof(IQueryable<>)) || Mentions(method.ReturnType, typeof(IQueryable)))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}");
        var saveMembers = repositories
            .SelectMany(type => type.GetMethods())
            .Where(method => method.Name.StartsWith("Save", StringComparison.Ordinal))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}");

        // Assert
        Assert.Empty(outsideAbstractions);
        Assert.Empty(queryableMembers);
        Assert.Empty(saveMembers);
    }

    /// <summary>Whether <paramref name="type"/> is, or wraps at any depth (Task, generic arguments), the given type.</summary>
    private static bool Mentions(Type type, Type target) =>
        type == target
        || (type.IsGenericType && type.GetGenericTypeDefinition() == target)
        || type.GetInterfaces().Any(contract => contract == target)
        || (type.IsGenericType && type.GetGenericArguments().Any(argument => Mentions(argument, target)));
}
