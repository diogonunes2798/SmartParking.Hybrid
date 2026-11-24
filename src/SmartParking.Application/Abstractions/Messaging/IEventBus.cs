namespace SmartParking.Application.Abstractions.Messaging;

/// <summary>
/// Event bus port (D2). Default in the prototype: PostgreSQL outbox + Redis Streams;
/// the implementation arrives in F3/F4. Delivery is at-least-once, so consumers must be idempotent.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : class;
}
