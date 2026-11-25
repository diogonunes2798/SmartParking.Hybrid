using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Sessions;

internal static class SessionErrors
{
    public static Error SessionNotFound(Guid id) =>
        Error.NotFound("Session.NotFound", $"Parking session {id} does not exist.");

    public static Error SpotNotFound(Guid id) =>
        Error.NotFound("Spot.NotFound", $"Parking spot {id} does not exist.");

    public static Error VehicleNotFound(Guid id) =>
        Error.NotFound("Vehicle.NotFound", $"Vehicle {id} does not exist.");

    public static Error PaymentMethodNotFound(Guid id) =>
        Error.NotFound("PaymentMethod.NotFound", $"Payment method {id} does not exist.");

    /// <summary>Temporary until F3b, which fulfils reservations (decision 2 of plan 002a).</summary>
    public static Error ReservationNotSupported { get; } = Error.Conflict(
        "Session.ReservationNotSupported",
        "Opening a session with a reservation is not available yet (F3b).");

    public static Error SpotInactive { get; } = Error.Conflict(
        "Session.SpotInactive", "The parking spot is inactive.");

    public static Error FacilityInactive { get; } = Error.Conflict(
        "Session.FacilityInactive", "The parking facility is inactive.");

    public static Error FacilityClosed { get; } = Error.Conflict(
        "Session.FacilityClosed", "The parking facility is closed at this time.");

    public static Error NoTariffInForce { get; } = Error.Conflict(
        "Session.NoTariffInForce", "The parking facility has no tariff in force.");

    public static Error VehicleHasOpenSession { get; } = Error.Conflict(
        "Session.VehicleHasOpenSession", "The vehicle already has an open session.");

    public static Error NotOpen { get; } = Error.Conflict(
        "Session.NotOpen", "Only an open session can be closed.");

    public static Error NotClosed { get; } = Error.Conflict(
        "Session.NotClosed", "Only a closed session can be paid.");

    public static Error PaymentMethodOfAnotherUser { get; } = Error.Conflict(
        "Session.PaymentMethodOfAnotherUser", "The payment method does not belong to the owner of the session's vehicle.");

    /// <summary>A stale <c>xmin</c> on save: the transition commands carry no <c>If-Match</c>, so 409 instead of 412.</summary>
    public static Error ConcurrentChange { get; } = Error.Conflict(
        "Session.ConcurrentChange", "The session was changed by another request. Read it again.");

    public static Error PaymentDeclined(Guid paymentId) => Error.PaymentDeclined(
        "Session.PaymentDeclined", "The payment gateway declined the payment. The session stays closed.", paymentId);

    /// <summary>The unit of work answers a stale <c>xmin</c> with 412; a transition without <c>If-Match</c> answers 409.</summary>
    public static Error FromSave(Error error) =>
        error.Type == ErrorType.PreconditionFailed ? ConcurrentChange : error;
}
