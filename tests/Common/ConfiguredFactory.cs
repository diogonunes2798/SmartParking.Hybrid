using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace SmartParking.Tests.Common;

/// <summary>
/// WebApplicationFactory for one scenario: environment, host settings and extra services.
/// Settings use <c>UseSetting</c>, so they are visible when Program.cs reads configuration.
/// </summary>
public sealed class ConfiguredFactory<TEntryPoint>(
    IReadOnlyDictionary<string, string?> settings,
    string environment = "Testing",
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }
}
