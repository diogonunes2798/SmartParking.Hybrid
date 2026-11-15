using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

public enum ParkingSessionStatus
{
    Open = 1,
    Closed = 2,
    Paid = 3,
}

/// <summary>
/// Parking session (spec §3, UC-02). Where it is stored is decision D1 (default: relational, behind
/// <c>IParkingSessionRepository</c>). The UC-02 endpoints and the outbox arrive in Phase 2b.
/// State machine: Open → Closed → Paid.
/// </summary>
public sealed class ParkingSession : IVersioned
{
    private ParkingSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid SpotId { get; private set; }

    public Guid VehicleId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Tariff applied when the session was closed.</summary>
    public Guid? TariffId { get; private set; }

    /// <summary>Amount due (EUR), known once the session is closed.</summary>
    public decimal? Cost { get; private set; }

    public ParkingSessionStatus Status { get; private set; }

    public uint Version { get; private set; }

    public static ParkingSession Open(Guid spotId, Guid vehicleId, Guid userId, DateTimeOffset startedAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            SpotId = Guard.Id(spotId, nameof(spotId)),
            VehicleId = Guard.Id(vehicleId, nameof(vehicleId)),
            UserId = Guard.Id(userId, nameof(userId)),
            StartedAt = Guard.Utc(startedAt),
            Status = ParkingSessionStatus.Open,
        };

    public void Close(DateTimeOffset endedAt, Guid tariffId, decimal cost)
    {
        if (Status != ParkingSessionStatus.Open)
        {
            throw new DomainValidationException("status", $"cannot close a session in state {Status}");
        }

        var end = Guard.Utc(endedAt);
        if (end < StartedAt)
        {
            throw new DomainValidationException(nameof(endedAt), "must not be earlier than startedAt");
        }

        if (cost < 0)
        {
            throw new DomainValidationException(nameof(cost), "must not be negative");
        }

        EndedAt = end;
        TariffId = Guard.Id(tariffId, nameof(tariffId));
        Cost = cost;
        Status = ParkingSessionStatus.Closed;
    }

    public void MarkPaid()
    {
        if (Status != ParkingSessionStatus.Closed)
        {
            throw new DomainValidationException("status", $"cannot pay a session in state {Status}");
        }

        Status = ParkingSessionStatus.Paid;
    }
}
