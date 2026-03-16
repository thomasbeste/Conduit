using Conduit.Messaging.InMemory;
using Conduit.Messaging.RabbitMq;
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

        if (config.UseInMemoryTransport)
        {
            RegisterInMemory(services, config);
        }
        else
        {
            RegisterRabbitMq(services, config);
        }

        return services;
    }

    private static void RegisterRabbitMq(IServiceCollection services, MessagingConfiguration config)
    {
        var settings = config.RabbitMqSettings
            ?? throw new InvalidOperationException("RabbitMQ settings are required. Call UseRabbitMq() in configuration.");

        // Register all consumer types in DI
        foreach (var reg in config.ConsumerRegistrations)
        {
            services.AddScoped(reg.ConsumerType);
        }

        // Register the bus as singleton (owns connection lifecycle)
        services.AddSingleton<IMessageBus>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<RabbitMqMessageBus>>();
            return new RabbitMqMessageBus(settings, config.ServiceName, config.ConsumerRegistrations, sp, logger);
        });

        // Register publisher (resolves from bus)
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>().Publisher);

        // Register hosted service to manage bus lifecycle
        services.AddHostedService<MessageBusHostedService>();
    }

    private static void RegisterInMemory(IServiceCollection services, MessagingConfiguration config)
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
        services.AddHostedService<MessageBusHostedService>();
    }
}

/// <summary>
/// Hosted service that starts/stops the message bus with the application.
/// </summary>
public sealed class MessageBusHostedService(IMessageBus bus) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => bus.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => bus.StopAsync(cancellationToken);
}

/// <summary>
/// Fluent configuration for Conduit.Messaging.
/// </summary>
public sealed class MessagingConfiguration
{
    internal RabbitMqSettings? RabbitMqSettings { get; private set; }
    internal bool UseInMemoryTransport { get; private set; }
    internal List<ConsumerRegistration> ConsumerRegistrations { get; } = [];

    /// <summary>
    /// Service name used as queue prefix (e.g., "service-audit").
    /// </summary>
    public string ServiceName { get; set; } = "default";

    /// <summary>
    /// Configures RabbitMQ as the transport.
    /// </summary>
    public void UseRabbitMq(Action<RabbitMqSettings> configure)
    {
        var settings = new RabbitMqSettings();
        configure(settings);
        RabbitMqSettings = settings;
        UseInMemoryTransport = false;
    }

    /// <summary>
    /// Configures RabbitMQ with pre-built settings.
    /// </summary>
    public void UseRabbitMq(RabbitMqSettings settings)
    {
        RabbitMqSettings = settings;
        UseInMemoryTransport = false;
    }

    /// <summary>
    /// Configures in-memory transport (for testing).
    /// </summary>
    public void UseInMemory()
    {
        UseInMemoryTransport = true;
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
