using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartParking.SensorSimulator.Provisioning;
using SmartParking.SensorSimulator.Sending;
using SmartParking.SensorSimulator.Wire;

namespace SmartParking.SensorSimulator;

/// <summary>
/// The simulator's services, shared by <c>Program</c> and the tests: the named Api client of
/// <see cref="IHttpClientFactory"/>, the clock and the three operations. The tests call this method too and only
/// replace the client's primary handler with the test server's.
/// </summary>
internal static class SimulatorServices
{
    public static IServiceCollection AddSensorSimulator(this IServiceCollection services, Uri apiBaseAddress)
    {
        services.AddHttpClient(ApiWire.ClientName, client => client.BaseAddress = apiBaseAddress);
        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<Provisioner>();
        services.AddTransient<IngestionSender>();
        services.AddTransient<GatewayRunner>();
        return services;
    }
}
