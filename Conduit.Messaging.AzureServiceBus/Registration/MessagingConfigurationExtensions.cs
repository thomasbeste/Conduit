using Conduit.Messaging.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.AzureServiceBus.Registration;

/// <summary>
/// Extension methods for configuring Azure Service Bus as the Conduit.Messaging transport.
/// </summary>
public static class MessagingConfigurationExtensions
{
    extension(MessagingConfiguration config)
    {
        /// <summary>
        /// Configures Azure Service Bus as the transport.
        /// </summary>
        public void UseAzureServiceBus(AzureServiceBusSettings settings)
        {
            config.TransportRegistrar = (services, cfg) => RegisterAzureServiceBus(services, cfg, settings);
        }

        /// <summary>
        /// Configures Azure Service Bus as the transport with a settings builder.
        /// </summary>
        public void UseAzureServiceBus(Action<AzureServiceBusSettings> configure)
        {
            var settings = new AzureServiceBusSettings();
            configure(settings);
            config.UseAzureServiceBus(settings);
        }
    }

    private static void RegisterAzureServiceBus(IServiceCollection services, MessagingConfiguration config, AzureServiceBusSettings settings)
    {
        // Register all consumer types in DI
        foreach (var reg in config.ConsumerRegistrations)
        {
            services.AddScoped(reg.ConsumerType);
        }

        // Register the bus as singleton
        services.AddSingleton<IMessageBus>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AzureServiceBusMessageBus>>();
            return new AzureServiceBusMessageBus(settings, config.ServiceName, config.ConsumerRegistrations, sp, logger);
        });

        // Register publisher
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>().Publisher);

        // Register stats provider
        services.AddSingleton<IMessagingStatsProvider, AzureServiceBusStatsProvider>();
    }
}
