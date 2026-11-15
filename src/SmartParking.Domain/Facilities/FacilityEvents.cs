using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

// Domain events of RF-06 that the availability read model needs (spec §4 «Configuração», Phase 4, ADR-0008).
// Written to the outbox in the same transaction as the change (ADR-0004); the outbox relay (Phase 5) publishes
// them on the configuration topic, where the read model projector consumes them. Each event carries the full
// state the read model needs (not a diff), so applying the latest one is enough and order within one aggregate
// is decided by OccurredAt.

/// <summary>A facility was created or updated. Carries what the read model stores (no operator, no personal data).</summary>
public sealed record FacilityChanged(
    Guid FacilityId,
    string Name,
    double Latitude,
    double Longitude,
    int Capacity,
    FacilityType Type,
    string Zone,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid AggregateId => FacilityId;
}

/// <summary>A facility was deleted (only possible without spots: the foreign key restricts it).</summary>
public sealed record FacilityDeleted(Guid FacilityId, DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid AggregateId => FacilityId;
}

/// <summary>A spot was created or updated.</summary>
public sealed record SpotChanged(Guid SpotId, Guid FacilityId, VehicleType AllowedVehicleType, DateTimeOffset OccurredAt)
    : IDomainEvent
{
    public Guid AggregateId => SpotId;
}

/// <summary>A spot was deleted.</summary>
public sealed record SpotDeleted(Guid SpotId, Guid FacilityId, DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid AggregateId => SpotId;
}
