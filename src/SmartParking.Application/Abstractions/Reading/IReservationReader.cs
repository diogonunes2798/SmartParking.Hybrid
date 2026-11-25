namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read port of the reservation queries (D10). Projection and <c>AsNoTracking</c>.</summary>
public interface IReservationReader
{
    Task<ReservationView?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Identifiers of the <c>Active</c> reservations that start before <paramref name="cutoff"/>, oldest first
    /// (the expiry worker passes <c>now − ExpiryGrace</c>).
    /// </summary>
    Task<IReadOnlyList<Guid>> ListDueIdsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
