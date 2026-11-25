namespace SmartParking.Infrastructure.Relational.Auditing;

/// <summary>Kind of change recorded in <c>audit_entries.operation</c> (stored as text).</summary>
internal enum AuditOperation
{
    Created,
    Updated,
    Deleted,
}
