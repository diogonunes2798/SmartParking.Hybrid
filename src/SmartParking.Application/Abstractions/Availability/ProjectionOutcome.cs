namespace SmartParking.Application.Abstractions.Availability;

/// <summary>What the availability projector did with one occupancy event (spec 004, «Ordem e idempotência»).</summary>
public enum ProjectionOutcome
{
    /// <summary>The event's (timestamp, eventId) pair is newer than the stored one; the pair was replaced.</summary>
    Applied,

    /// <summary>The event is the last one applied to its spot; nothing changed.</summary>
    Duplicate,

    /// <summary>The event's pair is not newer than the stored one; nothing changed.</summary>
    Stale,

    /// <summary>The spot is not in the view (created after the last rebuild, or inactive); nothing changed.</summary>
    UnknownSpot,
}
