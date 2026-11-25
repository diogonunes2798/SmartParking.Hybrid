using Npgsql;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Schema;

/// <summary>
/// AC-1 (spec 002): the OpenSessionPerVehicle migration adds the partial unique index of the open sessions per
/// vehicle, checked in the database as the application login. No uniqueness per spot.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class OpenSessionPerVehicleIndexTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Migrate_CreatesPartialUniqueIndex_AC1()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var definition = await DbProbe.ScalarAsync<string>(
            fixture.Databases.OwnerConnectionString,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_parking_sessions_vehicle_open'",
            cancellationToken);
        var foreignKeyIndexes = await DbProbe.ScalarAsync<long>(
            fixture.Databases.OwnerConnectionString,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_parking_sessions_vehicle_id'",
            cancellationToken);

        // Assert
        Assert.StartsWith("CREATE UNIQUE INDEX ux_parking_sessions_vehicle_open ON public.parking_sessions", definition, StringComparison.Ordinal);
        Assert.EndsWith("(vehicle_id) WHERE ((status)::text = 'Open'::text)", definition, StringComparison.Ordinal);
        Assert.Equal(1, foreignKeyIndexes);
    }

    [Fact]
    public async Task TwoOpenSessionsSameVehicle_AsAppUser_Fail23505_AC1()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var graph = await SessionGraph.SeedAsync(App, Start, cancellationToken);

        // Act
        var error = await DbProbe.FailsAsync(App, SessionGraph.InsertSession, cancellationToken,
            Guid.CreateVersion7(), graph.SpotId, graph.VehicleId, graph.TariffId, Start.AddMinutes(5), "Open");

        // Assert
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_parking_sessions_vehicle_open", error.ConstraintName);
    }

    [Fact]
    public async Task ClosedAndOpenSessionSameVehicle_AsAppUser_AreAccepted_AC1()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var graph = await SessionGraph.SeedAsync(App, Start, cancellationToken);
        await DbProbe.ExecuteAsync(App, "UPDATE parking_sessions SET status = 'Closed', ended_at = $2, amount = 1.50 WHERE id = $1",
            cancellationToken, graph.SessionId, Start.AddHours(1));

        // Act
        var inserted = await DbProbe.ExecuteAsync(App, SessionGraph.InsertSession, cancellationToken,
            Guid.CreateVersion7(), graph.SpotId, graph.VehicleId, graph.TariffId, Start.AddHours(2), "Open");

        // Assert
        Assert.Equal(1, inserted);
    }
}
