using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Application.Features.Spots;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Spots;

/// <summary>
/// AC-15: 1 to 1 000 spots; codes and hardware ids unique within the request (plan 001b, point 4). Checked before
/// the database is touched; conflicts with existing rows are left to the unique indexes (409, nothing created).
/// The list rules stop at the first failure, so a missing list or a null item is a 400 and never reaches the later rules.
/// </summary>
internal sealed class SpotBatchRequestValidator : AbstractValidator<SpotBatchRequest>
{
    public SpotBatchRequestValidator()
    {
        RuleFor(request => request.Spots)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(spots => spots!.All(spot => spot is not null)).WithMessage("A batch cannot contain null spots.")
            .Must(spots => spots!.Count is >= 1 and <= CreateSpotBatch.MaxItems)
            .WithMessage($"A batch has between 1 and {CreateSpotBatch.MaxItems} spots.")
            .Must(HaveDistinctCodes).WithMessage("Each code can appear only once in the batch.")
            .Must(HaveDistinctHardwareIds).WithMessage("Each sensor hardwareId can appear only once in the batch.")
            .OverridePropertyName("spots");

        RuleForEach(request => request.Spots)
            .ChildRules(item =>
            {
                item.RuleFor(entry => entry.ZoneId).NotEmpty().OverridePropertyName("zoneId");
                item.RuleFor(entry => entry.Code).NotEmpty().MaximumLength(ParkingSpot.CodeMaxLength).OverridePropertyName("code");
                item.RuleFor(entry => entry.Type).MustBeNameOf<SpotBatchItemRequest, SpotType>("type");
                item.RuleFor(entry => entry.Sensor!.HardwareId)
                    .NotEmpty().MaximumLength(Sensor.HardwareIdMaxLength)
                    .OverridePropertyName("sensor.hardwareId")
                    .When(entry => entry.Sensor is not null);
                item.RuleFor(entry => entry.Sensor!.Model)
                    .NotEmpty().MaximumLength(Sensor.ModelMaxLength)
                    .OverridePropertyName("sensor.model")
                    .When(entry => entry.Sensor is not null);
                item.RuleFor(entry => entry.Sensor!.InstalledAt)
                    .NotNull()
                    .OverridePropertyName("sensor.installedAt")
                    .When(entry => entry.Sensor is not null);
            })
            .OverridePropertyName("spots")
            .When(request => request.Spots is { Count: <= CreateSpotBatch.MaxItems } spots && spots.All(spot => spot is not null));
    }

    private static bool HaveDistinctCodes(IReadOnlyList<SpotBatchItemRequest>? spots)
    {
        var codes = (spots ?? []).Where(spot => spot?.Code is not null).Select(spot => spot.Code!.Trim()).ToList();
        return codes.Distinct(StringComparer.Ordinal).Count() == codes.Count;
    }

    private static bool HaveDistinctHardwareIds(IReadOnlyList<SpotBatchItemRequest>? spots)
    {
        var ids = (spots ?? []).Where(spot => spot?.Sensor?.HardwareId is not null).Select(spot => spot.Sensor!.HardwareId!.Trim()).ToList();
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
    }
}
