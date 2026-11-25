using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Tariffs;

/// <summary>Reads the tariff version in force now (UC-02); 404 when the facility does not exist or has none in force.</summary>
public static class GetCurrentTariff
{
    public sealed record Query(Guid FacilityId);

    internal sealed class Handler(ITariffReader reader, TimeProvider clock) : IQueryHandler<Query, TariffView>
    {
        public async Task<Result<TariffView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var version = await reader.GetCurrentAsync(query.FacilityId, clock.GetUtcNow(), cancellationToken);
            return version is null ? TariffErrors.NoCurrentVersion(query.FacilityId) : version;
        }
    }
}
