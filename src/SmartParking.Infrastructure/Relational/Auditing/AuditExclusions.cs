using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Auditing;

/// <summary>
/// Properties never copied into <c>audit_entries.before</c> or <c>.after</c>. The payment provider's token is a
/// credential: it never appears in responses, audit, outbox or logs (spec 002, AC-4).
/// </summary>
internal static class AuditExclusions
{
    private static readonly HashSet<(Type Entity, string Property)> Excluded =
    [
        (typeof(PaymentMethod), nameof(PaymentMethod.ProviderToken)),
    ];

    public static bool IsExcluded(Type entity, string property) => Excluded.Contains((entity, property));
}
