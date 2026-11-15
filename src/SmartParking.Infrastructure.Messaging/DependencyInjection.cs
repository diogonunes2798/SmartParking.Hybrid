using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartParking.Application.Abstractions;
using StackExchange.Redis;
using System.Diagnostics.Metrics;

namespace SmartParking.Infrastructure.Messaging;

/// <summary>
/// Registers the event bus (D2 default: Redis Streams, ADR-0006). The bus uses the same Redis server as the read
/// model (connection string <c>Redis</c>): with the D2 default there is no separate broker container.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Key under <c>ConnectionStrings</c> (the same Redis as Infrastructure.Cache).</summary>
    public const string ConnectionStringName = "Redis";

    /// <summary>Configuration section of the consumers: <c>Messaging:Consumers:&lt;name&gt;</c>.</summary>
    public const string ConsumersSection = "Messaging:Consumers";

    public static IServiceCollection AddEventBus(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{ConnectionStringName}'.");

        // Shared with Infrastructure.Cache when both are registered (whichever comes first creates it); same
        // settings: start even if Redis is down and keep reconnecting (RNF-03 scenario).
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEventBus, RedisStreamsEventBus>();
        return services;
    }

    /// <summary>
    /// Adds a hosted consumer group reader that hands batches to <typeparamref name="THandler"/> (registered as
    /// scoped here). Settings come from <c>Messaging:Consumers:<paramref name="name"/></c>, over the defaults
    /// passed in <paramref name="defaults"/>.
    /// </summary>
    public static IServiceCollection AddStreamConsumer<THandler>(
        this IServiceCollection services,
        IConfiguration configuration,
        string name,
        Action<RedisStreamConsumerOptions> defaults)
        where THandler : class, IMessageBatchHandler
    {
        ArgumentNullException.ThrowIfNull(defaults);

        var options = new RedisStreamConsumerOptions();
        defaults(options);
        configuration.GetSection($"{ConsumersSection}:{name}").Bind(options);
        options.Validate();

        services.AddEventBus(configuration);
        services.TryAddScoped<THandler>();
        services.AddSingleton<IHostedService>(sp => new RedisStreamConsumer<THandler>(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            sp.GetRequiredService<ILogger<RedisStreamConsumer<THandler>>>(),
            sp.GetRequiredService<IMeterFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
