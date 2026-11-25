using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Features.Ingestion;

namespace SmartParking.Application;

public static class DependencyInjection
{
    private static readonly Type[] HandlerInterfaces = [typeof(ICommandHandler<,>), typeof(IQueryHandler<,>)];

    /// <summary>Registers every command handler, query handler and validator found in the given assemblies.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services, params Assembly[] assemblies)
    {
        Assembly[] scanned = [typeof(DependencyInjection).Assembly, .. assemblies];

        foreach (var assembly in scanned.Distinct())
        {
            AddHandlers(services, assembly);
            AddValidators(services, assembly);
        }

        // Instruments of the SmartParking.Ingestion meter (spec 003); IMeterFactory comes from the host.
        services.TryAddSingleton<IngestionMetrics>();

        return services;
    }

    private static void AddHandlers(IServiceCollection services, Assembly assembly)
    {
        var registrations =
            from type in assembly.DefinedTypes
            where type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
            from contract in type.ImplementedInterfaces
            where contract.IsGenericType && HandlerInterfaces.Contains(contract.GetGenericTypeDefinition())
            select (contract, type.AsType());

        foreach (var (contract, implementation) in registrations)
        {
            services.AddScoped(contract, implementation);
        }
    }

    private static void AddValidators(IServiceCollection services, Assembly assembly)
    {
        // Internal validators too: API request validators are internal, like every type not used outside its project.
        foreach (var result in AssemblyScanner.FindValidatorsInAssembly(assembly, includeInternalTypes: true))
        {
            services.AddScoped(result.InterfaceType, result.ValidatorType);
        }
    }
}
