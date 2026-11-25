using System.Reflection;
using SmartParking.Domain.Common;
using static SmartParking.ArchitectureTests.SolutionAssemblies;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>Domain events are immutable facts: sealed records in Domain, each with its outbox type (spec 001, AC-21).</summary>
public sealed class DomainEventRulesTests
{
    private static readonly Assembly[] Assemblies = [DomainAssembly, ApplicationAssembly, InfrastructureAssembly];

    private static List<Type> DomainEvents { get; } = [.. Assemblies
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => type is { IsInterface: false, IsAbstract: false } && type.IsAssignableTo(typeof(IDomainEvent)))];

    [Fact]
    public void DomainEvents_AreSealedRecordsInDomain_AC21()
    {
        // Arrange
        Assert.NotEmpty(DomainEvents);

        // Act
        var violations = DomainEvents
            .Where(type => type.Assembly != DomainAssembly || !type.IsSealed || !IsRecord(type))
            .Select(type => type.FullName);

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void DomainEvents_EachHasOneUniqueOutboxType_AC21()
    {
        // Act
        var types = DomainEvents
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<DomainEventAttribute>()))
            .ToList();

        // Assert
        Assert.Empty(types.Where(entry => entry.Attribute is null).Select(entry => entry.Type.FullName));
        var names = types.Select(entry => entry.Attribute!.TypeName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The compiler gives every record a <c>&lt;Clone&gt;$</c> method.</summary>
    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;
}
