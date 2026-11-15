using SmartParking.Application.Abstractions;
using SmartParking.Application.Common;
using SmartParking.Application.Configuration;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Tariffs;
using SmartParking.Domain.Users;

namespace SmartParking.Application.Sessions;

/// <summary>
/// Command side of UC-02 (CQRS, plain handler, no mediator; ADR-0001). Each command ends with a single
/// <see cref="IUnitOfWork.SaveChangesAsync"/>: one transaction that writes the session/payment, the audit
/// rows and the outbox rows of the domain events (ADR-0004). Concurrency: the store's token (PostgreSQL
/// <c>xmin</c>) rejects a write based on a stale read (409); the optional <c>version</c> lets a client
/// reject a stale view up front.
/// </summary>
public sealed class SessionCommands(
    IParkingSessionRepository sessions,
    IRepository<ParkingSpot> spots,
    IRepository<Vehicle> vehicles,
    IRepository<Payment> payments,
    ITariffLookup tariffs,
    ITariffPricing pricing,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string Resource = "Session";

    public async Task<SessionDto> StartAsync(StartSession command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await spots.ExistsAsync(command.SpotId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("Spot", command.SpotId);
        }

        var vehicle = await vehicles.GetAsync(command.VehicleId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Vehicle", command.VehicleId);

        // The owner of the vehicle exists (foreign key), so checking ownership also checks the user.
        if (vehicle.UserId != command.UserId)
        {
            throw new DomainValidationException("vehicleId", "does not belong to the user");
        }

        // A second open session on the same spot is rejected by ux_parking_sessions_spot_id_open (409),
        // which also holds under concurrent requests; no read-then-check here.
        var session = ParkingSession.Open(command.SpotId, command.VehicleId, command.UserId, timeProvider.GetUtcNow());
        sessions.Add(session);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SessionDto.From(session);
    }

    public async Task<SessionDto> CloseAsync(Guid id, CloseSession command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await LoadAsync(id, command.Version, cancellationToken).ConfigureAwait(false);
        session.EnsureCanClose();

        // Priced with the tariff in force when the session started.
        // TODO(Diogo): confirm with Chapter 3 (start instant vs. splitting across tariff periods).
        var tariff = await tariffs.FindInForceForSpotAsync(session.SpotId, session.StartedAt, cancellationToken).ConfigureAwait(false)
            ?? throw new NoTariffInForceException(
                $"No tariff is in force for the zone of spot '{session.SpotId}' at {session.StartedAt:O}.");

        var endedAt = timeProvider.GetUtcNow();
        session.Close(endedAt, tariff.Id, pricing.Price(tariff.Rules, session.StartedAt, endedAt));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SessionDto.From(session);
    }

    public async Task<PaymentDto> PayAsync(Guid id, PaySession command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await LoadAsync(id, command.Version, cancellationToken).ConfigureAwait(false);
        var payment = session.Pay(command.Method, timeProvider.GetUtcNow());
        payments.Add(payment);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return PaymentDto.From(payment);
    }

    private async Task<ParkingSession> LoadAsync(Guid id, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        if (expectedVersion is { } version)
        {
            Concurrency.EnsureVersion(session, version, Resource);
        }

        return session;
    }
}
