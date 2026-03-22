namespace Conduit.Messaging;

/// <summary>
/// Transport-agnostic messaging statistics provider.
/// Implemented by each transport (RabbitMQ, Azure Service Bus, InMemory).
/// </summary>
public interface IMessagingStatsProvider
{
    /// <summary>
    /// Gets current messaging statistics (queues, message counts, rates).
    /// </summary>
    Task<MessagingStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Transport-agnostic messaging statistics.
/// </summary>
public class MessagingStats
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Transport { get; set; } = string.Empty;
    public List<QueueStats> Queues { get; set; } = [];
}

/// <summary>
/// Statistics for a single queue/subscription.
/// </summary>
public class QueueStats
{
    public string Name { get; set; } = string.Empty;
    public long MessagesReady { get; set; }
    public long MessagesUnacknowledged { get; set; }
    public long TotalMessages { get; set; }
    public int Consumers { get; set; }
    public MessageRateStats MessageStats { get; set; } = new();
    public string State { get; set; } = string.Empty;
    public string? IdleSince { get; set; }
    public long Memory { get; set; }
}

/// <summary>
/// Message throughput rates.
/// </summary>
public class MessageRateStats
{
    public double PublishRate { get; set; }
    public double DeliverRate { get; set; }
    public double AckRate { get; set; }
    public long TotalPublished { get; set; }
    public long TotalDelivered { get; set; }
    public long TotalAcknowledged { get; set; }
}
