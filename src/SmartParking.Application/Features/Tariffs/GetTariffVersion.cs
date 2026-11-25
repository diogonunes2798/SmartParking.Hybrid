using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Tariffs;

/// <summary>Reads one tariff version of a facility with its version (ETag); 404 when it is not in that facility.</summary>
public static class GetTariffVersion
{
    public sealed record Query(Guid FacilityId, Guid TariffId);

    internal sealed class Handler(ITariffReader reader) : IQueryHandler<Query, TariffView>
    {
        public async Task<Result<TariffView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var version = await reader.GetAsync(query.FacilityId, query.TariffId, cancellationToken);
            return version is null ? TariffErrors.NotFound(query.FacilityId, query.TariffId) : version;
        }
    }
}
