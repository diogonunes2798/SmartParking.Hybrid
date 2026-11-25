using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Tariffs;

internal static class TariffErrors
{
    public static Error NotFound(Guid facilityId, Guid tariffId) =>
        Error.NotFound("Tariff.NotFound", $"Tariff version {tariffId} does not exist in parking facility {facilityId}.");

    public static Error NoCurrentVersion(Guid facilityId) =>
        Error.NotFound("Tariff.NoCurrentVersion", $"Parking facility {facilityId} does not exist or has no tariff in force.");

    /// <summary>AC-9: a new version cannot start in the past.</summary>
    public static Error StartsInThePast { get; } = Error.Validation(
        "Tariff.StartsInThePast",
        "A new tariff version cannot start in the past.",
        new Dictionary<string, string[]> { ["validFrom"] = ["Must be now or later."] });

    public static Error NotAfterLatest { get; } = Error.Conflict(
        "Tariff.NotAfterLatest",
        "A new tariff version must start after the start of the latest version of the facility.");

    /// <summary>Plan 001b, point 2: a concurrent version change surfaces as 409, because a POST carries no If-Match.</summary>
    public static Error ConcurrentChange { get; } = Error.Conflict(
        "Tariff.ConcurrentChange",
        "Another tariff version of this facility was created or deleted at the same time. Read the versions again and retry.");

    public static Error NotDeletable { get; } = Error.Conflict(
        "Tariff.NotDeletable",
        "Only the latest tariff version, before it starts, can be deleted.");
}
