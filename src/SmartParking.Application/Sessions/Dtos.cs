using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Sessions;

/// <summary>Opens a session (UC-02). The start instant is the server clock, not the client's.</summary>
public sealed record StartSession(Guid SpotId, Guid VehicleId, Guid UserId);

/// <summary>Closes a session. <paramref name="Version"/> is optional: when sent, a stale value gives 409.</summary>
public sealed record CloseSession(uint? Version = null);

/// <summary>Pays the amount due of a closed session (simulated payment, spec §10).</summary>
public sealed record PaySession(PaymentMethod Method, uint? Version = null);

public sealed record SessionDto(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    Guid UserId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    Guid? TariffId,
    decimal? Cost,
    ParkingSessionStatus Status,
    uint Version)
{
    public static SessionDto From(ParkingSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.SpotId, s.VehicleId, s.UserId, s.StartedAt, s.EndedAt, s.TariffId, s.Cost, s.Status, s.Version);
    }
}

public sealed record PaymentDto(
    Guid Id, Guid SessionId, decimal Amount, PaymentMethod Method, PaymentStatus Status, DateTimeOffset Timestamp, uint Version)
{
    public static PaymentDto From(Payment p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Id, p.SessionId, p.Amount, p.Method, p.Status, p.Timestamp, p.Version);
    }
}

/// <summary>Query side of UC-02 (no-tracking reads).</summary>
public interface ISessionQueries
{
    Task<SessionDto?> GetSessionAsync(Guid id, CancellationToken cancellationToken);
}
