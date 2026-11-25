using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Reservations;

/// <summary>Reads one reservation with its current status (spec 002 AC-30); 404 when it does not exist.</summary>
public static class GetReservation
{
    public sealed record Query(Guid Id);

    internal sealed class Handler(IReservationReader reader) : IQueryHandler<Query, ReservationView>
    {
        public async Task<Result<ReservationView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var reservation = await reader.GetAsync(query.Id, cancellationToken);
            if (reservation is null)
            {
                return ReservationErrors.ReservationNotFound(query.Id);
            }

            return reservation;
        }
    }
}
