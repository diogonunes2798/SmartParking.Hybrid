using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Tariffs;

/// <summary>
/// Creates the next tariff version of a facility (RF-06, UC-02; ADR 0005). It starts now or later (else 400, AC-9) and
/// after the start of the latest version (else 409), and closes the latest version at its start. The unit of work saves
/// the new version, the closed one, one audit entry and one <c>TariffVersionCreated.v1</c> in one transaction (AC-7).
/// A concurrent change of the latest version comes back as 412 from the unit of work and is answered as 409.
/// </summary>
public static class CreateTariffVersion
{
    public sealed record Command(
        Guid FacilityId,
        DateTimeOffset ValidFrom,
        decimal PricePerHour,
        int BillingIncrementMinutes,
        decimal? DailyMaximum,
        int FreeMinutes,
        string Currency);

    internal sealed class Handler(
        IParkingFacilityRepository facilities,
        ITariffRepository tariffs,
        IUnitOfWork unitOfWork,
        TimeProvider clock) : ICommandHandler<Command, TariffView>
    {
        public async Task<Result<TariffView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await facilities.ExistsAsync(command.FacilityId, cancellationToken))
            {
                return FacilityErrors.NotFound(command.FacilityId);
            }

            var now = clock.GetUtcNow();
            if (command.ValidFrom < now)
            {
                return TariffErrors.StartsInThePast;
            }

            var latest = await tariffs.GetLatestAsync(command.FacilityId, cancellationToken);
            if (latest is not null && command.ValidFrom <= latest.ValidFrom)
            {
                return TariffErrors.NotAfterLatest;
            }

            var version = Tariff.CreateVersion(
                Guid.CreateVersion7(now), command.FacilityId, latest, command.ValidFrom, command.PricePerHour,
                command.BillingIncrementMinutes, command.DailyMaximum, command.FreeMinutes, command.Currency, now);
            tariffs.Add(version);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error switch
            {
                null => TariffView.From(version),
                { Type: ErrorType.PreconditionFailed } => TariffErrors.ConcurrentChange,
                { } error => error,
            };
        }
    }
}
