namespace SmartParking.Domain.Common;

/// <summary>
/// Name and version of a domain event, written to <c>outbox_messages.type</c>
/// as <c>{Name}.v{Version}</c> (for example <c>FacilityCreated.v1</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DomainEventAttribute(string name, int version) : Attribute
{
    public string Name { get; } = name;

    public int Version { get; } = version;

    public string TypeName => $"{Name}.v{Version}";
}
