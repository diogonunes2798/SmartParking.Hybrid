using Npgsql;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Domain.Facilities;

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

    /// <summary>Facility → zone → spot, user → vehicle and payment method, tariff, and one open session; all as the application login.</summary>
    private async Task<SessionGraph> SeedAsync(CancellationToken cancellationToken)
    {
        var graph = new SessionGraph(
            FacilityId: Guid.CreateVersion7(),
            ZoneId: Guid.CreateVersion7(),
            SpotId: Guid.CreateVersion7(),
            UserId: Guid.CreateVersion7(),
            VehicleId: Guid.CreateVersion7(),
            PaymentMethodId: Guid.CreateVersion7(),
            TariffId: Guid.CreateVersion7(),
            SessionId: Guid.CreateVersion7());
        var suffix = graph.FacilityId.ToString("N")[..12].ToUpperInvariant();

        await DbProbe.ExecuteAsync(App,
            """
            INSERT INTO parking_facilities (id, operator_id, name, address, location, time_zone, status)
            VALUES ($1, $2, $3, 'Rua da Sofia', 'SRID=4326;POINT(-8.41 40.2)'::geography, 'Europe/Lisbon', 'Active')
            """,
            cancellationToken, graph.FacilityId, Operator.ReferenceOperatorId, $"Sessions {suffix}");
        await DbProbe.ExecuteAsync(App, "INSERT INTO zones (id, facility_id, name) VALUES ($1, $2, 'Z1')",
            cancellationToken, graph.ZoneId, graph.FacilityId);
        await DbProbe.ExecuteAsync(App,
            "INSERT INTO parking_spots (id, facility_id, zone_id, code, type, status) VALUES ($1, $2, $3, 'A-001', 'Standard', 'Active')",
            cancellationToken, graph.SpotId, graph.FacilityId, graph.ZoneId);
        await DbProbe.ExecuteAsync(App, "INSERT INTO users (id, name, email) VALUES ($1, 'Driver', $2)",
            cancellationToken, graph.UserId, $"driver.{suffix.ToLowerInvariant()}@example.test");
        await DbProbe.ExecuteAsync(App, "INSERT INTO vehicles (id, user_id, license_plate) VALUES ($1, $2, $3)",
            cancellationToken, graph.VehicleId, graph.UserId, suffix);
        await DbProbe.ExecuteAsync(App,
            "INSERT INTO payment_methods (id, user_id, type, provider_token, masked_number) VALUES ($1, $2, 'Card', 'tok_test', '**** 4242')",
            cancellationToken, graph.PaymentMethodId, graph.UserId);
        await DbProbe.ExecuteAsync(App,
            """
            INSERT INTO tariffs (id, facility_id, valid_from, price_per_hour, billing_increment_minutes, free_minutes, currency)
            VALUES ($1, $2, $3, 1.50, 15, 0, 'EUR')
            """,
            cancellationToken, graph.TariffId, graph.FacilityId, Ten.AddDays(-30));
        await DbProbe.ExecuteAsync(App,
            "INSERT INTO parking_sessions (id, spot_id, vehicle_id, tariff_id, started_at, status) VALUES ($1, $2, $3, $4, $5, 'Open')",
            cancellationToken, graph.SessionId, graph.SpotId, graph.VehicleId, graph.TariffId, Ten.AddDays(-1));

        return graph;
    }

    private sealed record SessionGraph(
        Guid FacilityId,
        Guid ZoneId,
        Guid SpotId,
        Guid UserId,
        Guid VehicleId,
        Guid PaymentMethodId,
        Guid TariffId,
        Guid SessionId);
}
