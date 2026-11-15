using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Configuration;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Relational;

namespace SmartParking.Api.IntegrationTests.Relational;

/// <summary>
/// Phase 2a fixture: the API with its real engines (<see cref="ApiFixture"/>) and the relational schema
/// created by the EF Core migrations, applied exactly as <c>dotnet ef database update</c> would.
/// Shared by the Phase 2a test classes; each test creates its own data (unique names), so tests do not
/// depend on each other's rows.
/// </summary>
public sealed class RelationalFixture : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFixture _api = new();

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _api.Factory.Services;

    /// <summary>Connection string of the PostgreSQL test container (server-level tests, e.g. a fresh database).</summary>
    public string PostgresConnectionString => _api.Postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _api.InitializeAsync();
        Client = _api.Factory.CreateClient();

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _api.DisposeAsync();
    }

    /// <summary>Runs <paramref name="action"/> with a fresh DbContext (own scope, no tracking leftovers).</summary>
    public async Task<T> WithDbAsync<T>(Func<SmartParkingDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>());
    }

    // ---- API helpers ----

    public static GeoJsonPoint Coimbra => new("Point", [-8.4103, 40.2033]);

    public async Task<FacilityDto> CreateFacilityAsync(string? name = null, GeoJsonPoint? location = null, CancellationToken ct = default)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/facilities",
            new CreateFacility(
                name ?? $"Facility {Guid.NewGuid():N}", location ?? Coimbra, 120, FacilityType.MultiStorey, "ISEC", "Centro"),
            Json,
            ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FacilityDto>(Json, ct))!;
    }

    public async Task<SpotDto> CreateSpotAsync(Guid facilityId, string? code = null, CancellationToken ct = default)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/facilities/{facilityId}/spots",
            new CreateSpot(code ?? $"S-{Guid.NewGuid():N}"[..12], VehicleType.Car),
            Json,
            ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SpotDto>(Json, ct))!;
    }

    public async Task<SensorDto> CreateSensorAsync(Guid spotId, CancellationToken ct = default)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/sensors", new CreateSensor(spotId, "MagSense M2", "1.4.0", SensorStatus.Active), Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SensorDto>(Json, ct))!;
    }
}

[CollectionDefinition(Name)]
public sealed class RelationalTestGroup : ICollectionFixture<RelationalFixture>
{
    public const string Name = "relational";
}
