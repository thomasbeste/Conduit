using Conduit.Messaging.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.RabbitMq.Registration;

/// <summary>
/// Extension methods for configuring RabbitMQ as the Conduit.Messaging transport.
/// </summary>
public static class MessagingConfigurationExtensions
{
    /// <summary>
    /// Configures RabbitMQ as the transport with pre-built settings.
    /// </summary>
    public static void UseRabbitMq(this MessagingConfiguration config, RabbitMqSettings settings)
    {
        config.TransportRegistrar = (services, cfg) => RegisterRabbitMq(services, cfg, settings);
    }

    /// <summary>
    /// Configures RabbitMQ as the transport with a settings builder.
    /// </summary>
    public static void UseRabbitMq(this MessagingConfiguration config, Action<RabbitMqSettings> configure)
    {
        var settings = new RabbitMqSettings();
        configure(settings);
        config.UseRabbitMq(settings);
    }

    private static void RegisterRabbitMq(IServiceCollection services, MessagingConfiguration config, RabbitMqSettings settings)
    {
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
    }
}
