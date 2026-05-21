using Conduit.Messaging.Bridge;
using Conduit.Messaging.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.Registration;

/// <summary>
/// Extension methods for registering Conduit.Messaging services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Conduit.Messaging to the service collection.
    /// </summary>
    public static IServiceCollection AddConduitMessaging(
        this IServiceCollection services,
        Action<MessagingConfiguration> configure)
    {
        var config = new MessagingConfiguration();
        configure(config);

        // Expose the populated config (ServiceName + ConsumerRegistrations) via DI
        // so tooling — e.g. the per-service `--emit-subscription-manifest` mode
        // each Program.cs uses to publish its ASB subscription list — can read
        // the registration set without re-running the configure callback.
        services.AddSingleton(config);

        if (config.TransportRegistrar is not null)
        {
            config.TransportRegistrar(services, config);
        }
        else
        {
            // Default: in-memory transport
            RegisterInMemory(services, config);
        }

        // When enabled, replace IMessagePublisher with a decorator that auto-propagates
        // pipeline context (baggage, causality, correlation) into message headers.
        if (config.PropagateContextHeaders)
        {
            var existing = services.LastOrDefault(d => d.ServiceType == typeof(IMessagePublisher));
            if (existing is not null)
            {
                services.Remove(existing);
                services.AddSingleton<IMessagePublisher>(sp =>
                    new ContextPropagatingPublisher(sp.GetRequiredService<IMessageBus>()));
            }
        }

        // Register hosted service to manage bus lifecycle
        services.AddHostedService<MessageBusHostedService>();

        // Register messaging health check
        services.AddHealthChecks().AddCheck<MessagingHealthCheck>("messaging", tags: ["ready"]);

        return services;
    }

    internal static void RegisterInMemory(IServiceCollection services, MessagingConfiguration config)
    {
        // Register all consumer types in DI
        foreach (var reg in config.ConsumerRegistrations)
        {
            services.AddScoped(reg.ConsumerType);
        }

        // Register InMemoryMessageBus
        services.AddSingleton<InMemoryMessageBus>(sp =>
        {
            var bus = new InMemoryMessageBus(sp);
            foreach (var reg in config.ConsumerRegistrations)
            {
                bus.AddBinding(reg.MessageType, reg.ConsumerType);
            }
            return bus;
        });

        services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<InMemoryMessageBus>());
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>().Publisher);
        services.AddSingleton<IMessagingStatsProvider, InMemoryStatsProvider>();
    }
}

/// <summary>
/// Hosted service that starts/stops the message bus with the application.
/// Returns immediately so app startup isn't blocked; the bus's own retry loop
/// handles slow brokers (e.g., k8s sidecar still coming up). Cancellation is
/// driven by the host application lifetime, not the StartAsync token, so a
/// late-arriving connection still completes wiring (publisher channel +
/// consumers) instead of dying mid-handshake on a token that already expired.
/// </summary>
public sealed class MessageBusHostedService(
    IMessageBus bus,
    IHostApplicationLifetime appLifetime,
    ILogger<MessageBusHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget — the bus has its own retry/timeout budget. Tying
        // bus startup to `cancellationToken` (the host's "block app startup"
        // signal) caused: at 30s the token cancelled, the bus's connect kept
        // retrying with its own CTS, succeeded at ~70s, then CreateChannelAsync
        // was called with the already-cancelled token → OCE → _publisher null
        // forever. App-shutdown signalling now goes through ApplicationStopping.
        _ = Task.Run(async () =>
        {
            try
            {
                await bus.StartAsync(appLifetime.ApplicationStopping);
                logger.LogInformation("Message bus connected successfully");
            }
            catch (OperationCanceledException) when (appLifetime.ApplicationStopping.IsCancellationRequested)
            {
                // App shutting down before bus came up — expected, no log noise.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message bus failed to start — messaging will be unavailable");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => bus.StopAsync(cancellationToken);
}

/// <summary>
/// Fluent configuration for Conduit.Messaging.
/// </summary>
public sealed class MessagingConfiguration
{
    /// <summary>
    /// Transport registration delegate. Set by provider extensions (e.g., UseRabbitMq).
    /// When null, defaults to in-memory transport.
    /// </summary>
    public Action<IServiceCollection, MessagingConfiguration>? TransportRegistrar { get; set; }

    /// <summary>
    /// Consumer registrations (transport-agnostic).
    /// </summary>
    public List<ConsumerRegistration> ConsumerRegistrations { get; } = [];

    /// <summary>
    /// Service name used as queue prefix (e.g., "service-audit").
    /// </summary>
    public string ServiceName { get; set; } = "default";

    /// <summary>
    /// When true, decorates IMessagePublisher with <see cref="ContextPropagatingPublisher"/>
    /// that automatically extracts ambient PipelineContext into message headers on every publish/send.
    /// </summary>
    public bool PropagateContextHeaders { get; set; }

    /// <summary>
    /// Configures in-memory transport (for testing).
    /// </summary>
    public void UseInMemory()
    {
        TransportRegistrar = null; // null signals in-memory default
    }

    /// <summary>
    /// Registers a message consumer.
    /// </summary>
    public void AddConsumer<TConsumer>() where TConsumer : class
    {
        var consumerType = typeof(TConsumer);
        var messageType = FindMessageType(consumerType);
        ConsumerRegistrations.Add(new ConsumerRegistration
        {
            ConsumerType = consumerType,
            MessageType = messageType
        });
    }

    /// <summary>
    /// Registers all consumers from the specified assembly.
    /// </summary>
    public void AddConsumersFromAssembly(System.Reflection.Assembly assembly)
    {
        var consumerTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageConsumer<>)));

        foreach (var consumerType in consumerTypes)
        {
            var messageType = FindMessageType(consumerType);
            ConsumerRegistrations.Add(new ConsumerRegistration
            {
                ConsumerType = consumerType,
                MessageType = messageType
            });
        }
    }

    private static Type FindMessageType(Type consumerType)
    {
        var consumerInterface = consumerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageConsumer<>))
            ?? throw new InvalidOperationException(
                $"Type {consumerType.Name} does not implement IMessageConsumer<T>");

        return consumerInterface.GetGenericArguments()[0];
    }
}
