using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SmartParking.Infrastructure.Hosting;

/// <summary>
/// <c>/health/live</c>: process is up, no dependency is checked.
/// <c>/health/ready</c>: the checks tagged <see cref="HealthCheckTags.Ready"/> for the current persistence mode.
/// </summary>
internal sealed class HealthEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/health")
            .WithTags("Health")
            .AllowAnonymous();

        group.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        });

        group.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckTags.Ready),
            ResponseWriter = HealthReportWriter.WriteAsync,
        });
    }
}
