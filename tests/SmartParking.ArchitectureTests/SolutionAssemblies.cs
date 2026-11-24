using System.Reflection;

namespace SmartParking.ArchitectureTests;

internal static class SolutionAssemblies
{
    public const string DomainNamespace = "SmartParking.Domain";

    public const string ApplicationNamespace = "SmartParking.Application";

    public const string InfrastructureNamespace = "SmartParking.Infrastructure";

    public const string ApiNamespace = "SmartParking.Api";

    public const string WorkersNamespace = "SmartParking.Workers";

    public static Assembly DomainAssembly { get; } = typeof(SmartParking.Domain.AssemblyMarker).Assembly;

    public static Assembly ApplicationAssembly { get; } = typeof(SmartParking.Application.DependencyInjection).Assembly;

    public static Assembly InfrastructureAssembly { get; } = typeof(SmartParking.Infrastructure.DependencyInjection).Assembly;
}
