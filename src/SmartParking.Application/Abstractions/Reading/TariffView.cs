using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a tariff version, valid in [ValidFrom, ValidTo). <see cref="Version"/> is the <c>xmin</c>.</summary>
public sealed record TariffView(
    Guid Id,
    Guid FacilityId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    decimal PricePerHour,
    int BillingIncrementMinutes,
    decimal? DailyMaximum,
    int FreeMinutes,
    string Currency,
    uint Version)
{
    public static TariffView From(Tariff tariff)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        return new TariffView(
            tariff.Id, tariff.FacilityId, tariff.ValidFrom, tariff.ValidTo, tariff.PricePerHour, tariff.BillingIncrementMinutes,
            tariff.DailyMaximum, tariff.FreeMinutes, tariff.Currency, tariff.Version);
    }
}
