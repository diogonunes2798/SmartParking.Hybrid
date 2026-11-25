using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Tariffs;

/// <summary>
/// Deletes the latest tariff version while it is still in the future, and reopens the version it closed (AC-9;
/// ADR 0005). A version in force, a past one or one that is not the latest is 409. A version referenced by sessions is
/// kept by the FK (409). Needs the version the client read (428/412).
/// </summary>
public static class DeleteTariffVersion
{
    public sealed record Command(Guid FacilityId, Guid TariffId, uint? ExpectedVersion);

    internal sealed class Handler(ITariffRepository tariffs, IUnitOfWork unitOfWork, TimeProvider clock)
        : ICommandHandler<Command, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.ExpectedVersion is null)
            {
                return ConcurrencyCheck.VersionRequired;
            }

            var version = await tariffs.GetByIdAsync(command.TariffId, cancellationToken);
            if (version is null || version.FacilityId != command.FacilityId)
            {
                return TariffErrors.NotFound(command.FacilityId, command.TariffId);
            }

            if (ConcurrencyCheck.Verify(command.ExpectedVersion, version.Version) is { } stale)
            {
                return stale;
            }

            var now = clock.GetUtcNow();
            if (!version.CanBeDeleted(now))
            {
                return TariffErrors.NotDeletable;
            }

            var previous = await tariffs.GetClosedByAsync(version, cancellationToken);
            version.MarkForDeletion(previous, now);
            tariffs.Remove(version);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : version.Id;
        }
    }
}
