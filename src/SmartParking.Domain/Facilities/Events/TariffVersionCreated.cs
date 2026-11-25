using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A tariff version was created. Creating it closes the previous latest version at <c>ValidFrom</c>; that change belongs to this event (<c>PreviousVersionId</c>, <c>PreviousValidTo</c>) and has no event of its own (AC-7, ADR 0005).</summary>
[DomainEvent("TariffVersionCreated", 1)]
public sealed record TariffVersionCreated(
    Guid Id,
    Guid FacilityId,
    DateTimeOffset ValidFrom,
    decimal PricePerHour,
    int BillingIncrementMinutes,
    decimal? DailyMaximum,
    int FreeMinutes,
    string Currency,
    Guid? PreviousVersionId,
    DateTimeOffset? PreviousValidTo) : IDomainEvent;
