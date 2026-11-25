using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Reservations;

internal static class ReservationErrors
{
    public static Error ReservationNotFound(Guid id) =>
        Error.NotFound("Reservation.NotFound", $"Reservation {id} does not exist.");

    public static Error SpotNotFound(Guid id) =>
        Error.NotFound("Spot.NotFound", $"Parking spot {id} does not exist.");

    public static Error UserNotFound(Guid id) =>
        Error.NotFound("User.NotFound", $"User {id} does not exist.");

    public static Error VehicleNotFound(Guid id) =>
        Error.NotFound("Vehicle.NotFound", $"Vehicle {id} does not exist.");

    public static Error StartsInPast { get; } = Error.Validation(
        "Reservation.StartsInPast",
        "A reservation cannot start in the past.",
        new Dictionary<string, string[]> { ["startsAt"] = ["Must be now or later."] });

    public static Error TooLong(TimeSpan maximum) => Error.Validation(
        "Reservation.TooLong",
        "The reservation is longer than allowed.",
        new Dictionary<string, string[]> { ["endsAt"] = [$"The reservation may last at most {maximum.TotalHours:0.##} hours."] });

    public static Error TooFarAhead(TimeSpan maximum) => Error.Validation(
        "Reservation.TooFarAhead",
        "The reservation starts too far ahead.",
        new Dictionary<string, string[]> { ["startsAt"] = [$"Must be at most {maximum.TotalDays:0.##} days from now."] });

    public static Error OutsideOpeningHours { get; } = Error.Validation(
        "Reservation.OutsideOpeningHours",
        "The reservation must fit in a single opening interval of one day, in the facility's time zone.",
        new Dictionary<string, string[]> { ["startsAt"] = ["Outside the opening hours."], ["endsAt"] = ["Outside the opening hours."] });

    public static Error SpotInactive { get; } = Error.Conflict(
        "Reservation.SpotInactive", "The parking spot is inactive.");

    public static Error FacilityInactive { get; } = Error.Conflict(
        "Reservation.FacilityInactive", "The parking facility is inactive.");

    public static Error VehicleOfAnotherUser { get; } = Error.Conflict(
        "Reservation.VehicleOfAnotherUser", "The vehicle does not belong to the user of the reservation.");

    public static Error Overlaps { get; } = Error.Conflict(
        "Reservation.Overlaps", "The spot already has an active reservation that overlaps this interval.");

    public static Error NotActive { get; } = Error.Conflict(
        "Reservation.NotActive", "Only an active reservation can be cancelled.");

    /// <summary>A stale <c>xmin</c> on save: the transition commands carry no <c>If-Match</c>, so 409 instead of 412.</summary>
    public static Error ConcurrentChange { get; } = Error.Conflict(
        "Reservation.ConcurrentChange", "The reservation was changed by another request. Read it again.");

    /// <summary>The unit of work answers a stale <c>xmin</c> with 412; a transition without <c>If-Match</c> answers 409.</summary>
    public static Error FromSave(Error error) =>
        error.Type == ErrorType.PreconditionFailed ? ConcurrentChange : error;

    /// <summary>The <c>EXCLUDE</c> constraint (<c>23P01</c>) surfaces as a message the driver understands.</summary>
    public static Error FromCreate(Error error) =>
        error.Code == "Database.ExclusionViolation" ? Overlaps : error;
}
