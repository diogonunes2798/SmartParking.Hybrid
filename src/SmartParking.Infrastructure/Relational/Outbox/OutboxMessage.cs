namespace SmartParking.Infrastructure.Relational.Outbox;

/// <summary>
/// Domain event waiting to be published (D2). Written in the same transaction as the business change.
/// The outbox reader (F3/F4) sets <see cref="PublishedAt"/>; the application role cannot DELETE rows.
/// </summary>
internal sealed class OutboxMessage
{
    public const int TypeMaxLength = 100;

    public const int TraceIdMaxLength = 32;

    public Guid Id { get; init; }

    /// <summary>Event name and version, for example <c>FacilityCreated.v1</c>.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>The event serialised as JSON (stored as <c>jsonb</c>).</summary>
    public string Payload { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public string? TraceId { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
