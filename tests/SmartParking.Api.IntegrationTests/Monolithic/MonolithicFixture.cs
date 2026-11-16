using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartParking.Infrastructure.Relational;
using SmartParking.Infrastructure.Relational.Monolithic;

namespace SmartParking.Api.IntegrationTests.Monolithic;

/// <summary>
/// Phase 8 (ADR-0012): the API in <c>Persistence:Mode=Monolithic</c> whatever <see cref="TestMode.Current"/> is, over one
/// migrated PostgreSQL + PostGIS container and nothing else. The tests of the variant's own mechanisms (partitions,
/// <c>spot_state</c>, PostGIS plan, the UC-05 migration) therefore run in both test runs. The registered services are
/// captured, to prove that nothing of the hybrid sub-layers is in the container.
/// </summary>
public sealed class MonolithicFixture : IAsyncLifetime
{
    private static readonly IMeterFactory ProcessMeterFactory =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    private readonly ApiFixture _api = new(PersistenceMode.Monolithic);

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => Factory.Services;

    public IReadOnlyList<ServiceDescriptor> Registrations { get; private set; } = [];

    public string PostgresConnectionString => _api.Postgres.GetConnectionString();

    public NpgsqlDataSource DataSource => Services.GetRequiredService<NpgsqlDataSource>();

    public async ValueTask InitializeAsync()
    {
        await _api.InitializeAsync();
        var factory = _api.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                Registrations = [.. services];

                // Found in Phase 8: this host creates the same «SmartParking» instruments as the hybrid fixtures (the
                // PostgreSQL bus records the D7 latency, the relay the publish lag). When its own meters were disposed
                // with the host, OpenTelemetry stopped exporting the instruments of the same name in the other hosts
                // (same effect as in Phase 5), and a /metrics assertion of OutboxRelayTests failed in the full run only.
                // A meter factory that lives as long as the process avoids that disposal.
                services.AddSingleton(ProcessMeterFactory);
            }));
        Client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>().Database.MigrateAsync();
        Factory = factory;
    }

    /// <summary>The factory whose services the tests use (the one that captured <see cref="Registrations"/>).</summary>
    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _api.DisposeAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        var command = Factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        await using var _ = command;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

[CollectionDefinition(Name)]
public sealed class MonolithicTestGroup : ICollectionFixture<MonolithicFixture>
{
    public const string Name = "Monolithic variant (PostgreSQL + PostGIS only)";
}
