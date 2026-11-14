using System.Reflection;
using NetArchTest.Rules;

namespace SmartParking.ArchitectureTests;

/// <summary>
/// Fitness functions for the dependency rule of docs/adr/0001-solution-structure-and-layering.md:
/// dependencies point inwards (Domain ← Application ← Infrastructure.* ← hosts),
/// the Application layer is persistence-ignorant, and each storage sub-layer is independent of the others.
/// </summary>
public sealed class LayerDependencyTests
{
    private const string Domain = "SmartParking.Domain";
    private const string Application = "SmartParking.Application";
    private const string Infrastructure = "SmartParking.Infrastructure";
    private const string ServiceDefaults = "SmartParking.ServiceDefaults";
    private const string Api = "SmartParking.Api";
    private const string Workers = "SmartParking.Workers";

    /// <summary>Client libraries of the three storage engines (and EF Core, from Phase 2).</summary>
    private static readonly string[] StorageDrivers =
    [
        "Npgsql",
        "Microsoft.EntityFrameworkCore",
        "MongoDB",
        "StackExchange.Redis",
    ];

    public static TheoryData<string> InfrastructureAssemblies => new()
    {
        "SmartParking.Infrastructure.Relational",
        "SmartParking.Infrastructure.Documents",
        "SmartParking.Infrastructure.Cache",
        "SmartParking.Infrastructure.Messaging",
    };

    [Fact]
    public void Domain_does_not_depend_on_any_other_project()
    {
        var result = Types.InAssembly(typeof(Domain.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(Application, Infrastructure, ServiceDefaults, Api, Workers)
            .GetResult();

        AssertSuccessful(result);
    }

    [Fact]
    public void Domain_does_not_depend_on_storage_drivers()
    {
        var result = Types.InAssembly(typeof(Domain.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(StorageDrivers)
            .GetResult();

        AssertSuccessful(result);
    }

    [Fact]
    public void Application_depends_only_inwards()
    {
        var result = Types.InAssembly(typeof(Application.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(Infrastructure, ServiceDefaults, Api, Workers)
            .GetResult();

        AssertSuccessful(result);
    }

    [Fact]
    public void Application_is_persistence_ignorant()
    {
        var result = Types.InAssembly(typeof(Application.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(StorageDrivers)
            .GetResult();

        AssertSuccessful(result);
    }

    [Theory]
    [MemberData(nameof(InfrastructureAssemblies))]
    public void Infrastructure_sub_layers_are_independent_of_each_other_and_of_the_hosts(string assemblyName)
    {
        string[] forbidden =
        [
            .. InfrastructureAssemblies.Select(row => (string)row.Data).Where(name => name != assemblyName),
            ServiceDefaults,
            Api,
            Workers,
        ];

        var result = Types.InAssembly(Assembly.Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        AssertSuccessful(result);
    }

    [Fact]
    public void ServiceDefaults_does_not_depend_on_infrastructure_or_hosts()
    {
        var result = Types.InAssembly(typeof(ServiceDefaults.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(Infrastructure, Api, Workers)
            .GetResult();

        AssertSuccessful(result);
    }

    /// <summary>
    /// Endpoints must go through Application handlers (CQRS), never talk to a storage driver directly.
    /// Wiring the sub-layers through their DependencyInjection extensions is allowed.
    /// </summary>
    [Fact]
    public void Api_does_not_use_storage_drivers_directly()
    {
        var result = Types.InAssembly(typeof(Program).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(StorageDrivers)
            .GetResult();

        AssertSuccessful(result);
    }

    private static void AssertSuccessful(NetArchTest.Rules.TestResult result) =>
        Assert.True(
            result.IsSuccessful,
            "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []));
}
