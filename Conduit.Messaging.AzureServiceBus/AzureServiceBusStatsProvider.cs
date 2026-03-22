using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Messaging stats provider for Azure Service Bus.
/// Returns minimal stats — Azure Monitor / Application Insights is the
/// preferred way to monitor Service Bus queues in production.
/// </summary>
public class AzureServiceBusStatsProvider(
    ILogger<AzureServiceBusStatsProvider> logger) : IMessagingStatsProvider
{
    public Task<MessagingStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Azure Service Bus stats requested — returning transport info only");

        return Task.FromResult(new MessagingStats
        {
            Timestamp = DateTime.UtcNow,
            Transport = "AzureServiceBus",
            Queues = []
        });
    }
}
