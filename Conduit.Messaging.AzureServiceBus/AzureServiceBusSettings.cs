namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Configuration settings for Azure Service Bus connection.
/// </summary>
public class AzureServiceBusSettings
{
    public const string SectionName = "AzureServiceBus";

    /// <summary>
    /// Azure Service Bus connection string.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Topic name for pub/sub messages. Auto-created if it doesn't exist.
    /// </summary>
    public string TopicName { get; set; } = "gpi-events";

    /// <summary>
    /// Max concurrent calls per consumer.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 10;
}
