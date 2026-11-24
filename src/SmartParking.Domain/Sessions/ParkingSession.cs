using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

/// <summary>
/// Parking session of a vehicle on a spot, priced by one tariff version. Structure only in F2;
/// opening, closing and paying arrive in F3 (UC-02, RF-02).
/// </summary>
public sealed class ParkingSession : AggregateRoot
{
    private ParkingSession()
    {
    }

    public Guid SpotId { get; private init; }

    public Guid VehicleId { get; private init; }

    public Guid TariffId { get; private init; }

    public Guid? ReservationId { get; private init; }

    public DateTimeOffset StartedAt { get; private init; }

    public DateTimeOffset? EndedAt { get; private set; }

    public SessionStatus Status { get; private set; }

    public decimal? Amount { get; private set; }

    public uint Version { get; private set; }

    public static ParkingSession Start(
        Guid id,
        Guid spotId,
        Guid vehicleId,
        Guid tariffId,
        Guid? reservationId,
        DateTimeOffset startedAt) => new()
        {
            Id = Guard.Id(id),
            SpotId = Guard.Id(spotId),
            VehicleId = Guard.Id(vehicleId),
            TariffId = Guard.Id(tariffId),
            ReservationId = reservationId is { } reservation ? Guard.Id(reservation) : null,
            StartedAt = startedAt,
            Status = SessionStatus.Open,
        };
}
