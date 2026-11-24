using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SmartParking.Infrastructure.Relational.Auditing;

/// <summary>
/// JSON state of an aggregate for <c>audit_entries.before</c> and <c>.after</c>: the root's mapped properties
/// plus its owned collections (for example the facility's opening hours), read from the change tracker.
/// The row version (<c>xmin</c>) and the owner keys of owned rows are left out.
/// </summary>
internal static class AuditSnapshot
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>State before the change (original values), or <c>null</c> for an added row.</summary>
    public static string? Before(EntityEntry entry) =>
        entry.State == EntityState.Added ? null : Serialize(entry, original: true);

    /// <summary>State after the change (current values), or <c>null</c> for a deleted row.</summary>
    public static string? After(EntityEntry entry) =>
        entry.State == EntityState.Deleted ? null : Serialize(entry, original: false);

    private static string Serialize(EntityEntry entry, bool original) =>
        JsonSerializer.Serialize(Capture(entry, original), JsonOptions);

    private static Dictionary<string, object?> Capture(EntityEntry entry, bool original)
    {
        var values = original ? entry.OriginalValues : entry.CurrentValues;
        var state = new Dictionary<string, object?>();

        foreach (var property in entry.Metadata.GetProperties())
        {
            if (property.IsConcurrencyToken || property.IsShadowProperty())
            {
                continue;
            }

            state[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = values[property];
        }

        foreach (var navigation in entry.Metadata.GetNavigations().Where(navigation => navigation.TargetEntityType.IsOwned()))
        {
            state[JsonNamingPolicy.CamelCase.ConvertName(navigation.Name)] = OwnedRows(entry, navigation, original)
                .Select(child => Capture(child, original))
                .ToList();
        }

        return state;
    }

    /// <summary>
    /// Owned rows of this owner as they were (original: every row except added ones) or as they are
    /// (current: every row except deleted ones). Deleted rows are no longer in the navigation, so the
    /// change tracker is searched by the ownership foreign key.
    /// </summary>
    private static IEnumerable<EntityEntry> OwnedRows(EntityEntry owner, INavigation navigation, bool original)
    {
        var ownership = navigation.ForeignKey;
        var ownerKey = ownership.PrincipalKey.Properties.Select(property => owner.Property(property.Name).CurrentValue).ToArray();

        return owner.Context.ChangeTracker.Entries()
            .Where(child => child.Metadata == navigation.TargetEntityType)
            .Where(child => original ? child.State != EntityState.Added : child.State != EntityState.Deleted)
            .Where(child => ownership.Properties
                .Select(property => child.Property(property.Name).CurrentValue)
                .SequenceEqual(ownerKey));
    }
}
