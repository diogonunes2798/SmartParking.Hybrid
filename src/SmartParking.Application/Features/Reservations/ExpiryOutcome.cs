namespace SmartParking.Application.Features.Reservations;

/// <summary>What <see cref="ExpireReservation"/> did with one reservation.</summary>
public enum ExpiryOutcome
{
    /// <summary>Expired, with its audit entry and event.</summary>
    Expired,

    /// <summary>No longer <c>Active</c>, not yet due, or gone: nothing written.</summary>
    Skipped,
}
