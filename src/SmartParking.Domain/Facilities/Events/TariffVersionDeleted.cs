using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A future tariff version was deleted. The previous version, if any, is open again (<c>ReopenedVersionId</c>, <c>ValidTo = null</c>); that change belongs to this event (ADR 0005).</summary>
[DomainEvent("TariffVersionDeleted", 1)]
public sealed record TariffVersionDeleted(
    Guid Id,
    Guid FacilityId,
    DateTimeOffset ValidFrom,
    decimal PricePerHour,
    int BillingIncrementMinutes,
    decimal? DailyMaximum,
    int FreeMinutes,
    string Currency,
    Guid? ReopenedVersionId) : IDomainEvent;
