using Npgsql;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Schema;

/// <summary>
/// Constraints of the session aggregate, checked straight in the database as the application login
/// (spec 001, AC-19). Its behaviour arrives in F3; the schema already refuses inconsistent rows.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class SessionConstraintsTests(ApiFixture fixture)
{
    private const string InsertReservation =
        """
        INSERT INTO reservations (id, spot_id, user_id, vehicle_id, starts_at, ends_at, status)
        VALUES ($1, $2, $3, $4, $5, $6, 'Active')
        """;

    private const string InsertPayment =
        """
        INSERT INTO payments (id, session_id, payment_method_id, amount, status, created_at)
        VALUES ($1, $2, $3, 3.50, 'Succeeded', now())
        """;

    private static readonly DateTimeOffset Ten = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task OverlappingActiveReservations_AsAppUser_Fail23P01_AC19()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var graph = await SeedAsync(cancellationToken);
        await DbProbe.ExecuteAsync(App, InsertReservation, cancellationToken,
            Guid.CreateVersion7(), graph.SpotId, graph.UserId, graph.VehicleId, Ten, Ten.AddHours(2));

        // Act
        var error = await DbProbe.FailsAsync(App, InsertReservation, cancellationToken,
            Guid.CreateVersion7(), graph.SpotId, graph.UserId, graph.VehicleId, Ten.AddHours(1), Ten.AddHours(3));

        // Assert
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, error.SqlState);
        Assert.Equal("ex_reservations_active_overlap", error.ConstraintName);
    }

    [Fact]
    public async Task TwoSucceededPayments_AsAppUser_Fail23505_AC19()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var graph = await SeedAsync(cancellationToken);
        await DbProbe.ExecuteAsync(App, InsertPayment, cancellationToken, Guid.CreateVersion7(), graph.SessionId, graph.PaymentMethodId);

        // Act
        var error = await DbProbe.FailsAsync(App, InsertPayment, cancellationToken, Guid.CreateVersion7(), graph.SessionId, graph.PaymentMethodId);

        // Assert
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_payments_session_succeeded", error.ConstraintName);
    }

    [Fact]
    public async Task DeleteTariffWithSessions_AsAppUser_Fails23503_AC19()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var graph = await SeedAsync(cancellationToken);

        // Act
        var error = await DbProbe.FailsAsync(App, "DELETE FROM tariffs WHERE id = $1", cancellationToken, graph.TariffId);

        // Assert
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal("fk_parking_sessions_tariffs_tariff_id", error.ConstraintName);
    }

    private Task<SessionGraph> SeedAsync(CancellationToken cancellationToken) =>
        SessionGraph.SeedAsync(App, Ten.AddDays(-1), cancellationToken);
}
