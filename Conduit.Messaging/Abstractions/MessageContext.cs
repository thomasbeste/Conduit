namespace Conduit.Messaging;

/// <summary>
/// Context information for a consumed message.
/// </summary>
public sealed class MessageContext
{
    /// <summary>
    /// Unique identifier for this message.
    /// </summary>
    public required Guid MessageId { get; init; }

    /// <summary>
    /// The correlation ID for tracking related messages.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// ID of the conversation this message belongs to.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// When the message was sent.
    /// </summary>
    public DateTime? SentTime { get; init; }

    /// <summary>
    /// The source address/queue of the message.
    /// </summary>
    public string? SourceAddress { get; init; }

    /// <summary>
    /// The destination address/queue of the message.
    /// </summary>
    public string? DestinationAddress { get; init; }

    /// <summary>
    /// Headers/metadata associated with the message.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Headers { get; init; }

    /// <summary>
    /// Number of times this message has been delivered (for retry tracking).
    /// </summary>
    public int DeliveryCount { get; init; }

    /// <summary>
    /// The tenant ID if multi-tenant system.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The user ID who initiated the message.
    /// </summary>
    public string? UserId { get; init; }
}
