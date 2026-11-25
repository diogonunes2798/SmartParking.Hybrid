namespace SmartParking.Domain.Sessions;

/// <summary>
/// Time rules of a reservation (spec 002, «Ciclo da reserva»), built from the <c>Reservations</c> configuration.
/// The Domain reads no configuration: the handlers pass the policy in.
/// </summary>
public sealed record ReservationPolicy
{
    public ReservationPolicy(TimeSpan maxDuration, TimeSpan maxAdvance, TimeSpan earlyArrival, TimeSpan expiryGrace)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAdvance, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(earlyArrival, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(expiryGrace, TimeSpan.Zero);

        MaxDuration = maxDuration;
        MaxAdvance = maxAdvance;
        EarlyArrival = earlyArrival;
        ExpiryGrace = expiryGrace;
    }

    /// <summary>Longest allowed <c>endsAt − startsAt</c>.</summary>
    public TimeSpan MaxDuration { get; }

    /// <summary>Latest allowed <c>startsAt</c>, counted from now.</summary>
    public TimeSpan MaxAdvance { get; }

    /// <summary>How early, before <c>startsAt</c>, the vehicle may arrive and fulfil the reservation.</summary>
    public TimeSpan EarlyArrival { get; }

    /// <summary>How long after <c>startsAt</c> the reservation can still be fulfilled before it expires.</summary>
    public TimeSpan ExpiryGrace { get; }

    /// <summary>[startsAt − EarlyArrival, startsAt + ExpiryGrace], closed at both ends (spec 002).</summary>
    public FulfilmentWindow FulfilmentWindow(DateTimeOffset startsAt) =>
        new(startsAt - EarlyArrival, startsAt + ExpiryGrace);

    /// <summary>
    /// Whether an active reservation is due to expire: strictly after the end of its fulfilment window, so
    /// fulfilment and expiry never overlap.
    /// </summary>
    public bool IsDue(DateTimeOffset startsAt, DateTimeOffset now) => now > startsAt + ExpiryGrace;
}
