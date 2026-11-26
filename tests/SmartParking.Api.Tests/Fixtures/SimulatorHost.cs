using Microsoft.Extensions.DependencyInjection;
using SmartParking.SensorSimulator;
using SmartParking.SensorSimulator.Wire;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// The simulator's services (spec 003, F4b) wired to a test Api: the same <c>AddSensorSimulator</c> as the tool, with
/// only the primary handler of the named client replaced by the test server's, and optionally a
/// <see cref="RecordingHandler"/> in front of it.
/// </summary>
internal static class SimulatorHost
{
    public static ServiceProvider Create(ConfiguredFactory<Program> api, RecordingHandler? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSensorSimulator(api.Server.BaseAddress);
        UseTestServer(services, api, recorder);
        return services.BuildServiceProvider();
    }

    /// <summary>Points the simulator's named client at the test server; also given to <c>SimulatorCommand.Create</c>.</summary>
    public static void UseTestServer(IServiceCollection services, ConfiguredFactory<Program> api, RecordingHandler? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        var client = services.AddHttpClient(ApiWire.ClientName).ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
        if (recorder is not null)
        {
            client.AddHttpMessageHandler(() => recorder);
        }
    }
}
