using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Availability;

/// <summary>State of a spot in the read model (spec §3: «estado atual», only in the read model).</summary>
public enum SpotState
{
    Free = 1,
    Occupied = 2,
}

/// <summary>
/// One occupancy change to apply. <see cref="TimestampMs"/> is the sensor timestamp in Unix milliseconds (the
/// precision of the MongoDB log, ADR-0007); together with <see cref="EventId"/> it orders events of one spot.
/// </summary>
public sealed record OccupancyChange(Guid EventId, Guid SpotId, SpotState State, long TimestampMs);

/// <summary>Outcome of applying one occupancy change (metric tag and test oracle).</summary>
public enum ApplyOutcome
{
    /// <summary>The state changed: counters moved and the change was published.</summary>
    Applied = 1,

    /// <summary>Newer than the last applied event but the same state (e.g. OCCUPIED twice): only the timestamp moved.</summary>
    Unchanged = 2,

    /// <summary>The very event already applied (redelivery): nothing changed.</summary>
    Duplicate = 3,

    /// <summary>Older than the last applied event of the spot (arrived late): ignored.</summary>
    Stale = 4,

    /// <summary>The spot is not in the read model (not configured, or its configuration event has not arrived yet).</summary>
    UnknownSpot = 5,
}

/// <summary>Facility as stored in the read model (from <see cref="FacilityChanged"/> or from the relational store).</summary>
public sealed record FacilitySnapshot(
    Guid FacilityId, string Name, double Latitude, double Longitude, int Capacity, FacilityType Type, string Zone);

/// <summary>Spot as stored in the read model (configuration part; the state comes from occupancy events).</summary>
public sealed record SpotSnapshot(Guid SpotId, Guid FacilityId, VehicleType AllowedVehicleType);

/// <summary>Outcome of a configuration change (idempotency and ordering by <c>OccurredAt</c>).</summary>
public enum ConfigurationOutcome
{
    Applied = 1,

    /// <summary>Older than (or as old as) the last configuration change applied to the same facility or spot.</summary>
    Stale = 2,

    /// <summary>A delete of something the read model does not have.</summary>
    Unknown = 3,

    /// <summary>An event type the read model does not use (acknowledged without effect).</summary>
    Ignored = 4,
}

/// <summary>
/// Configuration writes and occupancy changes of the read model. Every operation is atomic per facility/spot and
/// idempotent, because the bus delivers at least once and, with several consumers, in no guaranteed order between
/// events of one spot (ADR-0006).
/// </summary>
public interface IReadModelWriter
{
    /// <summary>
    /// Applies occupancy changes, each atomically (spot state, facility, zone and vehicle-type counters, and the
    /// pub/sub notification), only if newer than the last applied event of the spot. Returns one outcome per change,
    /// in order. Throws on a transient store failure (nothing is assumed applied; reapplying is safe), and
    /// <see cref="ReadModelRebuildingException"/> while a rebuild holds the read model.
    /// </summary>
    Task<IReadOnlyList<ApplyOutcome>> ApplyAsync(IReadOnlyList<OccupancyChange> changes, CancellationToken cancellationToken);

    /// <param name="changedAt">
    /// When the configuration changed (the event's <c>OccurredAt</c>). A change not newer than the one stored for the
    /// same facility is ignored (redelivery, late arrival). Deletes win ties and leave a tombstone (ADR-0008).
    /// </param>
    Task<ConfigurationOutcome> UpsertFacilityAsync(FacilitySnapshot facility, DateTimeOffset changedAt, CancellationToken cancellationToken);

    Task<ConfigurationOutcome> DeleteFacilityAsync(Guid facilityId, DateTimeOffset changedAt, CancellationToken cancellationToken);

    /// <summary>A new spot starts FREE (Phase 4 decision, ADR-0008) until its first occupancy event.</summary>
    Task<ConfigurationOutcome> UpsertSpotAsync(SpotSnapshot spot, DateTimeOffset changedAt, CancellationToken cancellationToken);

    Task<ConfigurationOutcome> DeleteSpotAsync(Guid spotId, DateTimeOffset changedAt, CancellationToken cancellationToken);
}

/// <summary>
/// Write side of the availability read model (UC-03, RF-03; default store Redis, ADR-0008), as used by the
/// projectors. While a rebuild holds the read model (<see cref="BeginRebuildAsync"/>), their writes are refused with
/// <see cref="ReadModelRebuildingException"/> and change nothing.
/// </summary>
public interface IAvailabilityReadModel : IReadModelWriter
{
    /// <summary>When the read model was last fully loaded, or null if it never was (or was cleared).</summary>
    Task<DateTimeOffset?> GetLoadedAtAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes the rebuild lock (only one rebuild at a time, across processes) and returns the session through which the
    /// rebuild writes. Throws <see cref="RebuildInProgressException"/> if another rebuild holds it. Disposing the
    /// session releases the lock.
    /// </summary>
    Task<IReadModelRebuildSession> BeginRebuildAsync(CancellationToken cancellationToken);
}

/// <summary>The only writer allowed while a rebuild holds the read model (review of Phase 4, finding 1).</summary>
public interface IReadModelRebuildSession : IReadModelWriter, IAsyncDisposable
{
    /// <summary>Deletes every key of the read model (tombstones and the loaded mark included).</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    Task MarkLoadedAsync(DateTimeOffset loadedAt, CancellationToken cancellationToken);
}

/// <summary>A rebuild holds the read model: the write was refused and nothing changed. Transient: retry later.</summary>
public sealed class ReadModelRebuildingException : Exception
{
    public ReadModelRebuildingException()
        : base("The availability read model is being rebuilt; the write was refused.")
    {
    }

    public ReadModelRebuildingException(string message)
        : base(message)
    {
    }

    public ReadModelRebuildingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Another rebuild holds the lock.</summary>
public sealed class RebuildInProgressException : Exception
{
    public RebuildInProgressException()
        : base("Another rebuild of the availability read model is in progress.")
    {
    }

    public RebuildInProgressException(string message)
        : base(message)
    {
    }

    public RebuildInProgressException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One facility in an availability answer (UC-03).</summary>
/// <param name="Spots">Registered spots (of the requested vehicle type, if any).</param>
/// <param name="Free">Free spots (of the requested vehicle type, if any).</param>
public sealed record FacilityAvailability(
    Guid FacilityId,
    string Name,
    string Zone,
    FacilityType Type,
    double Latitude,
    double Longitude,
    int Capacity,
    double DistanceMeters,
    int Spots,
    int Free);

/// <summary>Query side of UC-03: reads only the read model, never the relational store (spec §4).</summary>
public interface IAvailabilityQueries
{
    /// <summary>Facilities within <see cref="AvailabilitySearch.RadiusMeters"/>, nearest first.</summary>
    Task<IReadOnlyList<FacilityAvailability>> SearchAsync(AvailabilitySearch search, CancellationToken cancellationToken);
}

/// <summary>
/// Source of the configuration to load into the read model (default: the relational store, RF-06). Streams
/// everything, without paging, because a rebuild reads it once from start to end.
/// </summary>
public interface IAvailabilitySource
{
    IAsyncEnumerable<FacilitySnapshot> GetFacilitiesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<SpotSnapshot> GetSpotsAsync(CancellationToken cancellationToken);
}
