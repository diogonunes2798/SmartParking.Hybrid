using SmartParking.Application.Features.Tariffs;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Tariffs;

/// <summary>Body of <c>POST /api/facilities/{facilityId}/tariffs</c>. The currency defaults to EUR.</summary>
internal sealed record CreateTariffRequest(
    DateTimeOffset? ValidFrom,
    decimal? PricePerHour,
    int? BillingIncrementMinutes,
    decimal? DailyMaximum,
    int? FreeMinutes,
    string? Currency)
{
    public CreateTariffVersion.Command ToCommand(Guid facilityId) => new(
        facilityId,
        ValidFrom!.Value,
        PricePerHour!.Value,
        BillingIncrementMinutes!.Value,
        DailyMaximum,
        FreeMinutes!.Value,
        Currency ?? Tariff.DefaultCurrency);
}
