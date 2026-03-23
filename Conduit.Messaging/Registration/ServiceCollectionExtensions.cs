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
/// Connects in the background so the host isn't blocked waiting for RabbitMQ.
/// </summary>
public sealed class MessageBusHostedService(IMessageBus bus, ILogger<MessageBusHostedService> logger) : IHostedService
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectionTimeout);
            await bus.StartAsync(timeoutCts.Token);
            logger.LogInformation("Message bus connected successfully");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Message bus did not connect within {Timeout}s — messaging may be unavailable until connection is established",
                ConnectionTimeout.TotalSeconds);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Message bus failed to start — messaging will be unavailable");
        }
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
