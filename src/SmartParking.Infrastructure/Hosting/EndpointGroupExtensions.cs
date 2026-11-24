using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SmartParking.Infrastructure.Hosting;

public static class EndpointGroupExtensions
{
    /// <summary>Registers every concrete <see cref="IEndpointGroup"/> defined in the assembly.</summary>
    public static IServiceCollection AddEndpointGroups(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var groups = assembly.DefinedTypes
            .Where(type => type is { IsAbstract: false, IsInterface: false } && type.IsAssignableTo(typeof(IEndpointGroup)));

        foreach (var group in groups)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IEndpointGroup), group));
        }

        return services;
    }

    /// <summary>Maps every registered <see cref="IEndpointGroup"/>. Program.cs calls this once.</summary>
    public static WebApplication MapEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var group in app.Services.GetServices<IEndpointGroup>())
        {
            group.Map(app);
        }

        return app;
    }
}
