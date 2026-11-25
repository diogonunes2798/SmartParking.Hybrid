namespace SmartParking.Infrastructure.Relational.Auditing;

/// <summary>
/// Append-only audit record of one change to one entity (RNF-06). Written in the same transaction as the
/// change and its outbox message. The application role can only INSERT and SELECT (spec 001, AC-10).
/// No foreign keys: the audited row may be deleted later.
/// </summary>
internal sealed class AuditEntry
{
    public const int ActorMaxLength = 200;

    public const int EntityTypeMaxLength = 100;

    public const int TraceIdMaxLength = 32;

    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string Actor { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid EntityId { get; init; }

    public AuditOperation Operation { get; init; }

    /// <summary>State before the change, as JSON; <c>null</c> on <see cref="AuditOperation.Created"/>.</summary>
    public string? Before { get; init; }

    /// <summary>State after the change, as JSON; <c>null</c> on <see cref="AuditOperation.Deleted"/>.</summary>
    public string? After { get; init; }

    public string? TraceId { get; init; }

    public Guid OutboxMessageId { get; init; }
}
