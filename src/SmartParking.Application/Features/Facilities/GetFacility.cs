using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Facilities;

/// <summary>Reads one facility with its opening hours and version (ETag); 404 when it does not exist.</summary>
public static class GetFacility
{
    public sealed record Query(Guid Id);

    internal sealed class Handler(IFacilityReader reader) : IQueryHandler<Query, FacilityView>
    {
        public async Task<Result<FacilityView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var facility = await reader.GetAsync(query.Id, cancellationToken);
            if (facility is null)
            {
                return FacilityErrors.NotFound(query.Id);
            }

            return facility;
        }
    }
}
