using Microsoft.AspNetCore.Routing;

namespace SmartParking.Infrastructure.Hosting;

/// <summary>
/// One group of endpoints per feature, in its own file. Groups are registered in DI
/// (<see cref="EndpointGroupExtensions.AddEndpointGroups"/>) and mapped by <c>app.MapEndpoints()</c>.
/// </summary>
public interface IEndpointGroup
{
    void Map(IEndpointRouteBuilder app);
}
