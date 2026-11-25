using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users.Events;

/// <summary>A driver registered. Name and email are personal data and are not in the payload (spec 002).</summary>
[DomainEvent("UserRegistered", 1)]
public sealed record UserRegistered(
    Guid Id) : IDomainEvent;
