using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartParking.Domain.Common;

namespace SmartParking.Infrastructure.Relational.Outbox;

/// <summary>
/// Transactional outbox (spec §5, ADR-0004). Before EF Core sends the changes, takes the pending domain
/// events of every tracked aggregate and adds one <see cref="OutboxMessage"/> per event to the same unit of
/// work. EF Core wraps <c>SaveChanges</c> in a transaction, so the change and its events commit or roll back
/// together, without a distributed transaction (no two-phase commit with the broker).
/// Registered before <c>AuditSaveChangesInterceptor</c>; the outbox rows themselves are not audited.
/// </summary>
/// <remarks>
/// Events are dequeued from the aggregates here. If <c>SaveChanges</c> fails they are not re-raised; that is
/// fine because the context is per request and there are no retries (no <c>EnableRetryOnFailure</c>). A
/// retry strategy would have to rebuild them, like the audit rows (diary of Phase 2a).
/// </remarks>
public sealed class OutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AddOutboxMessages(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AddOutboxMessages(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void AddOutboxMessages(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // The request's trace context (ASP.NET Core activity), so the relay can continue the trace (ADR-0009).
        var current = Activity.Current;
        var traceParent = current?.IdFormat == ActivityIdFormat.W3C ? current.Id : null;

        var messages = context.ChangeTracker.Entries<IHasDomainEvents>()
            .SelectMany(e => e.Entity.DequeueDomainEvents())
            .Select(e => ToMessage(e, traceParent))
            .ToList(); // materialise before adding to the tracker being enumerated

        context.AddRange(messages);
    }

    private static OutboxMessage ToMessage(IDomainEvent domainEvent, string? traceParent) => new()
    {
        Id = Guid.CreateVersion7(domainEvent.OccurredAt),
        Type = domainEvent.GetType().Name,
        AggregateId = domainEvent.AggregateId,
        Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), PayloadJson),
        OccurredAt = domainEvent.OccurredAt,
        TraceParent = traceParent,
    };
}
