using NetArchTest.Rules;
using static SmartParking.ArchitectureTests.SolutionAssemblies;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>Isolation of the Infrastructure sublayers inside one project (E1; spec 000, AC-2).</summary>
public sealed class SublayerIsolationTests
{
    private const string Relational = InfrastructureNamespace + ".Relational";
    private const string Documents = InfrastructureNamespace + ".Documents";
    private const string Cache = InfrastructureNamespace + ".Cache";
    private const string Hosting = InfrastructureNamespace + ".Hosting";
    private const string Payments = InfrastructureNamespace + ".Payments";

    [Fact]
    public void RelationalSublayer_Inspected_UsesNeitherMongoNorRedis_AC2()
    {
        // Arrange
        AssertHasTypes(Relational);

        // Act
        var result = Types.InAssembly(InfrastructureAssembly)
            .That().ResideInNamespace(Relational)
            .ShouldNot().HaveDependencyOnAny("MongoDB.Driver", "MongoDB.Bson", "StackExchange.Redis", Documents, Cache, Hosting)
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
    }

    [Theory]
    [InlineData(Documents, Cache)]
    [InlineData(Cache, Documents)]
    public void DocumentsAndCacheSublayers_Inspected_DoNotUseEfCore_AC2(string sublayer, string sibling)
    {
        // Arrange
        AssertHasTypes(sublayer);

        // Act
        var result = Types.InAssembly(InfrastructureAssembly)
            .That().ResideInNamespace(sublayer)
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", Relational, sibling, Hosting)
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
    }

    [Fact]
    public void PaymentsAdapter_Inspected_UsesNoPersistenceSublayer()
    {
        // Arrange (spec 002, plan 002a: the payment gateway lives outside the persistence sublayers)
        AssertHasTypes(Payments);

        // Act
        var result = Types.InAssembly(InfrastructureAssembly)
            .That().ResideInNamespace(Payments)
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore", "MongoDB.Driver", "MongoDB.Bson", "StackExchange.Redis", Relational, Documents, Cache)
            .GetResult();

        // Assert
        Assert.True(result.IsSuccessful, RuleFailures.Describe(result));
    }

    /// <summary>A rule over an empty namespace passes vacuously; make sure the sublayer exists.</summary>
    private static void AssertHasTypes(string ns) =>
        Assert.NotEmpty(Types.InAssembly(InfrastructureAssembly).That().ResideInNamespace(ns).GetTypes());
}
