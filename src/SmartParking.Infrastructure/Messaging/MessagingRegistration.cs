using Microsoft.Extensions.DependencyInjection;

namespace SmartParking.Infrastructure.Messaging;

/// <summary>
/// Event bus sublayer (D2: PostgreSQL outbox + Redis Streams). Extension point only in F1:
/// the <c>IEventBus</c> implementation and the outbox reader arrive in F3/F4.
/// </summary>
internal static class MessagingRegistration
{
    public static IServiceCollection AddMessaging(this IServiceCollection services) => services;
}
