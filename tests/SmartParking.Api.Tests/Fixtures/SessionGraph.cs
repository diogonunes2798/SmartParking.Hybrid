using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Facility → zone → spot, user → vehicle and payment method, tariff, and one open session, inserted by SQL as the
/// application login. Used by the schema tests, which check the constraints without going through the API.
/// </summary>
internal sealed record SessionGraph(
    Guid FacilityId,
    Guid ZoneId,
    Guid SpotId,
    Guid UserId,
    Guid VehicleId,
    Guid PaymentMethodId,
    Guid TariffId,
    Guid SessionId)
{
    public const string InsertSession =
        "INSERT INTO parking_sessions (id, spot_id, vehicle_id, tariff_id, started_at, status) VALUES ($1, $2, $3, $4, $5, $6)";

    public static async Task<SessionGraph> SeedAsync(string connectionString, DateTimeOffset sessionStart, CancellationToken cancellationToken)
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

        await DbProbe.ExecuteAsync(connectionString,
            """
            INSERT INTO parking_facilities (id, operator_id, name, address, location, time_zone, status)
            VALUES ($1, $2, $3, 'Rua da Sofia', 'SRID=4326;POINT(-8.41 40.2)'::geography, 'Europe/Lisbon', 'Active')
            """,
            cancellationToken, graph.FacilityId, Operator.ReferenceOperatorId, $"Sessions {suffix}");
        await DbProbe.ExecuteAsync(connectionString, "INSERT INTO zones (id, facility_id, name) VALUES ($1, $2, 'Z1')",
            cancellationToken, graph.ZoneId, graph.FacilityId);
        await DbProbe.ExecuteAsync(connectionString,
            "INSERT INTO parking_spots (id, facility_id, zone_id, code, type, status) VALUES ($1, $2, $3, 'A-001', 'Standard', 'Active')",
            cancellationToken, graph.SpotId, graph.FacilityId, graph.ZoneId);
        await DbProbe.ExecuteAsync(connectionString, "INSERT INTO users (id, name, email) VALUES ($1, 'Driver', $2)",
            cancellationToken, graph.UserId, $"driver.{suffix.ToLowerInvariant()}@example.test");
        await DbProbe.ExecuteAsync(connectionString, "INSERT INTO vehicles (id, user_id, license_plate) VALUES ($1, $2, $3)",
            cancellationToken, graph.VehicleId, graph.UserId, suffix);
        await DbProbe.ExecuteAsync(connectionString,
            "INSERT INTO payment_methods (id, user_id, type, provider_token, masked_number) VALUES ($1, $2, 'Card', 'tok_test', '**** 4242')",
            cancellationToken, graph.PaymentMethodId, graph.UserId);
        await DbProbe.ExecuteAsync(connectionString,
            """
            INSERT INTO tariffs (id, facility_id, valid_from, price_per_hour, billing_increment_minutes, free_minutes, currency)
            VALUES ($1, $2, $3, 1.50, 15, 0, 'EUR')
            """,
            cancellationToken, graph.TariffId, graph.FacilityId, sessionStart.AddDays(-30));
        await DbProbe.ExecuteAsync(connectionString, InsertSession,
            cancellationToken, graph.SessionId, graph.SpotId, graph.VehicleId, graph.TariffId, sessionStart, "Open");

        return graph;
    }
}
