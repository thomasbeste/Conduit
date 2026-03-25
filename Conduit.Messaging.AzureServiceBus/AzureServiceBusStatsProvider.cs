using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Messaging stats provider for Azure Service Bus.
/// Enumerates subscriptions on the configured topic and returns runtime properties
/// (active messages, dead-letter counts, etc.).
/// </summary>
public class AzureServiceBusStatsProvider(
    IOptions<AzureServiceBusSettings> options,
    ILogger<AzureServiceBusStatsProvider> logger) : IMessagingStatsProvider
{
    private ServiceBusAdministrationClient? _adminClient;

    private ServiceBusAdministrationClient GetAdminClient()
        => _adminClient ??= new ServiceBusAdministrationClient(options.Value.ConnectionString);

    public async Task<MessagingStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrEmpty(settings.ConnectionString))
        {
            logger.LogWarning("Azure Service Bus connection string not configured — returning empty stats");
            return new MessagingStats
            {
                Timestamp = DateTime.UtcNow,
                Transport = "AzureServiceBus",
                Queues = []
            };
        }

        try
        {
            var adminClient = GetAdminClient();
            var queues = new List<QueueStats>();

            // Enumerate subscriptions on the configured topic
            await foreach (var sub in adminClient.GetSubscriptionsAsync(settings.TopicName, cancellationToken))
            {
                try
                {
                    var runtime = await adminClient.GetSubscriptionRuntimePropertiesAsync(
                        settings.TopicName, sub.SubscriptionName, cancellationToken);

                    var props = runtime.Value;
                    var activeMessages = props.ActiveMessageCount;
                    var deadLetterMessages = props.DeadLetterMessageCount;
                    var transferDeadLetterMessages = props.TransferDeadLetterMessageCount;
                    var totalMessages = activeMessages + deadLetterMessages + transferDeadLetterMessages;

                    queues.Add(new QueueStats
                    {
                        Name = sub.SubscriptionName,
                        MessagesReady = activeMessages,
                        MessagesUnacknowledged = 0, // ASB doesn't expose this per-subscription
                        TotalMessages = totalMessages,
                        Consumers = 0, // ASB doesn't expose consumer count via admin API
                        MessageStats = new MessageRateStats
                        {
                            // ASB admin API doesn't provide rate metrics — Azure Monitor handles that
                            PublishRate = 0,
                            DeliverRate = 0,
                            AckRate = 0,
                            TotalPublished = 0,
                            TotalDelivered = 0,
                            TotalAcknowledged = 0
                        },
                        State = props.ActiveMessageCount > 0 ? "running" : "idle",
                        IdleSince = props.AccessedAt == default ? null : props.AccessedAt.ToString("O"),
                        Memory = 0 // Not available in ASB
                    });

                    // Also report dead-letter sub-queue if it has messages
                    if (deadLetterMessages > 0)
                    {
                        queues.Add(new QueueStats
                        {
                            Name = $"{sub.SubscriptionName}.dlq",
                            MessagesReady = deadLetterMessages,
                            TotalMessages = deadLetterMessages,
                            State = "dead-letter"
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to get runtime properties for subscription {Subscription}",
                        sub.SubscriptionName);
                }
            }

            logger.LogDebug("Azure Service Bus stats: {Count} subscriptions on topic {Topic}",
                queues.Count, settings.TopicName);

            return new MessagingStats
            {
                Timestamp = DateTime.UtcNow,
                Transport = "AzureServiceBus",
                Queues = queues
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch Azure Service Bus messaging stats");
            return new MessagingStats
            {
                Timestamp = DateTime.UtcNow,
                Transport = "AzureServiceBus",
                Queues = []
            };
        }
    }
}
