using SmartParking.Domain.Facilities;

namespace SmartParking.Tests.Common;

/// <summary>
/// Facility (Europe/Lisbon, 08:00–20:00 every day) → zone → spot, and user → vehicle, inserted by SQL as the
/// application login (plan 002b, proposal 14). Serves the Workers tests, which cannot call the Api, and lets a test
/// insert reservations in any status.
/// </summary>
internal sealed record ReservationGraph(Guid FacilityId, Guid ZoneId, Guid SpotId, Guid UserId, Guid VehicleId)
{
    public static async Task<ReservationGraph> SeedAsync(string connectionString, CancellationToken cancellationToken)
    {
        var graph = new ReservationGraph(
            FacilityId: Guid.CreateVersion7(),
            ZoneId: Guid.CreateVersion7(),
            SpotId: Guid.CreateVersion7(),
            UserId: Guid.CreateVersion7(),
            VehicleId: Guid.CreateVersion7());
        var suffix = graph.FacilityId.ToString("N")[^12..].ToUpperInvariant();

        await DbProbe.ExecuteAsync(connectionString,
            """
            INSERT INTO parking_facilities (id, operator_id, name, address, location, time_zone, status)
            VALUES ($1, $2, $3, 'Rua da Sofia', 'SRID=4326;POINT(-8.41 40.2)'::geography, 'Europe/Lisbon', 'Active')
            """,
            cancellationToken, graph.FacilityId, Operator.ReferenceOperatorId, $"Reservations {suffix}");
        await DbProbe.ExecuteAsync(connectionString,
            """
            INSERT INTO facility_opening_hours (facility_id, day_of_week, opens_at, closes_at)
            SELECT $1, day, '08:00', '20:00' FROM generate_series(0, 6) AS day
            """,
            cancellationToken, graph.FacilityId);
        await DbProbe.ExecuteAsync(connectionString, "INSERT INTO zones (id, facility_id, name) VALUES ($1, $2, 'Z1')",
            cancellationToken, graph.ZoneId, graph.FacilityId);
        await DbProbe.ExecuteAsync(connectionString,
            "INSERT INTO parking_spots (id, facility_id, zone_id, code, type, status) VALUES ($1, $2, $3, 'R-001', 'Standard', 'Active')",
            cancellationToken, graph.SpotId, graph.FacilityId, graph.ZoneId);
        await DbProbe.ExecuteAsync(connectionString, "INSERT INTO users (id, name, email) VALUES ($1, 'Driver', $2)",
            cancellationToken, graph.UserId, $"reserver.{suffix.ToLowerInvariant()}@example.test");
        await DbProbe.ExecuteAsync(connectionString, "INSERT INTO vehicles (id, user_id, license_plate) VALUES ($1, $2, $3)",
            cancellationToken, graph.VehicleId, graph.UserId, $"R{suffix}");

        return graph;
    }

    /// <summary>Inserts a reservation of this spot and vehicle, with no audit or outbox row (arrange only).</summary>
    public async Task<Guid> InsertReservationAsync(
        string connectionString, string status, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        await DbProbe.ExecuteAsync(connectionString,
            """
            INSERT INTO reservations (id, spot_id, user_id, vehicle_id, starts_at, ends_at, status)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """,
            cancellationToken, id, SpotId, UserId, VehicleId, startsAt, endsAt, status);
        return id;
    }
}
