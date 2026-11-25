using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Tariffs;

/// <summary>A tariff version on the wire, valid in [validFrom, validTo). The version travels in the <c>ETag</c> header.</summary>
internal sealed record TariffResponse(
    Guid Id,
    Guid FacilityId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    decimal PricePerHour,
    int BillingIncrementMinutes,
    decimal? DailyMaximum,
    int FreeMinutes,
    string Currency)
{
    public static TariffResponse From(TariffView view) => new(
        view.Id, view.FacilityId, view.ValidFrom, view.ValidTo, view.PricePerHour, view.BillingIncrementMinutes,
        view.DailyMaximum, view.FreeMinutes, view.Currency);
}
