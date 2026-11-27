using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Availability;

/// <summary>
/// Free spots per facility, zone and spot type (spec 004, «API Contract»; UC-03, RF-03). Reads only the Redis view.
/// With <see cref="Query.FacilityId"/>, a facility that is not in the view is a 404; with an
/// <see cref="Query.OperatorId"/> too, a facility of another operator gives an empty list. With only the operator, the
/// list has the operator's facilities (empty when it has none). Facilities and zones come sorted by name, then id.
/// </summary>
public static class GetAvailability
{
    public sealed record Query(Guid? FacilityId, Guid? OperatorId, Guid? ZoneId, SpotType? SpotType);

    internal sealed class Handler(IAvailabilityReader reader) : IQueryHandler<Query, IReadOnlyList<FacilityAvailability>>
    {
        public async Task<Result<IReadOnlyList<FacilityAvailability>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var read = query switch
            {
                { FacilityId: { } facilityId } => await reader.GetFacilityAsync(facilityId, query.SpotType, query.ZoneId, cancellationToken),
                { OperatorId: { } operatorId } => await reader.GetOperatorFacilitiesAsync(operatorId, query.SpotType, query.ZoneId, cancellationToken),
                _ => throw new ArgumentException("A facilityId or an operatorId is required; the API validator checks it.", nameof(query)),
            };

            if (read.Error is { } error)
            {
                return error;
            }

            if (query.FacilityId is { } requested && read.Value.Count == 0)
            {
                return Error.NotFound("Availability.FacilityNotFound", $"Facility {requested} is not in the availability view.");
            }

            return Result.Success<IReadOnlyList<FacilityAvailability>>(
            [
                .. read.Value
                    .Where(facility => query.OperatorId is not { } operatorId || facility.OperatorId == operatorId)
                    .OrderBy(facility => facility.Name, StringComparer.Ordinal)
                    .ThenBy(facility => facility.FacilityId)
                    .Select(facility => facility with
                    {
                        Zones = [.. facility.Zones.OrderBy(zone => zone.Name, StringComparer.Ordinal).ThenBy(zone => zone.ZoneId)],
                    }),
            ]);
        }
    }
}
