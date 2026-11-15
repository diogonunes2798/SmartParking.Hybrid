using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartParking.Application.Abstractions;
using SmartParking.Domain.Common;

namespace SmartParking.Infrastructure.Relational.Auditing;

/// <summary>
/// Audit log of RNF-06. Before EF Core sends the changes, adds one <see cref="AuditEntry"/> per created,
/// updated or deleted entity to the same unit of work. EF Core wraps <c>SaveChanges</c> in a transaction,
/// so a change and its audit row commit or roll back together: no audit without change and vice versa.
/// Properties marked <see cref="PersonalDataAttribute"/> are masked (GDPR).
/// </summary>
public sealed class AuditSaveChangesInterceptor(ICurrentActor actor, TimeProvider timeProvider) : SaveChangesInterceptor
{
    public const string Masked = "***";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AddAuditEntries(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AddAuditEntries(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void AddAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNow();

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.Entity is not AuditEntry
                && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => new AuditEntry
            {
                Id = Guid.CreateVersion7(now),
                OccurredAt = now,
                Actor = actor.Name,
                EntityType = e.Metadata.ClrType.Name,
                EntityId = (Guid)e.Property("Id").CurrentValue!,
                Operation = e.State switch
                {
                    EntityState.Added => "Created",
                    EntityState.Modified => "Updated",
                    _ => "Deleted",
                },
                Changes = JsonSerializer.Serialize(DescribeChanges(e)),
            })
            .ToList(); // materialise before adding to the tracker being enumerated

        context.AddRange(entries);
    }

    private static Dictionary<string, object?> DescribeChanges(EntityEntry entry)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        Collect(entry.Properties, entry.ComplexProperties, prefix: string.Empty, entry.State, changes);
        return changes;
    }

    private static void Collect(
        IEnumerable<PropertyEntry> properties,
        IEnumerable<ComplexPropertyEntry> complexProperties,
        string prefix,
        EntityState state,
        Dictionary<string, object?> changes)
    {
        foreach (var property in properties)
        {
            // The concurrency token (xmin) is a store artefact, not business data.
            if (property.Metadata.IsConcurrencyToken)
            {
                continue;
            }

            var name = prefix + JsonNamingPolicy.CamelCase.ConvertName(property.Metadata.Name);
            var personal = property.Metadata.PropertyInfo?.GetCustomAttribute<PersonalDataAttribute>() is not null;

            switch (state)
            {
                case EntityState.Added:
                    changes[name] = Value(property.CurrentValue, personal);
                    break;
                case EntityState.Deleted:
                    changes[name] = Value(property.OriginalValue, personal);
                    break;
                case EntityState.Modified when property.IsModified
                    && !Equals(property.OriginalValue, property.CurrentValue):
                    changes[name] = new { old = Value(property.OriginalValue, personal), @new = Value(property.CurrentValue, personal) };
                    break;
            }
        }

        foreach (var complex in complexProperties)
        {
            var nestedPrefix = prefix + JsonNamingPolicy.CamelCase.ConvertName(complex.Metadata.Name) + ".";
            Collect(complex.Properties, complex.ComplexProperties, nestedPrefix, state, changes);
        }
    }

    private static object? Value(object? value, bool personal) =>
        personal && value is not null ? Masked
        : value is Enum e ? e.ToString()
        : value;
}
