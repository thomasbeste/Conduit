namespace Conduit.Messaging;

/// <summary>
/// Base class for all messages with common metadata.
/// Inherit from this for strongly-typed message contracts.
/// </summary>
public abstract record MessageBase
{
    /// <summary>
    /// Unique identifier for this message instance.
    /// </summary>
    public Guid MessageId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// When the message was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Correlation ID for tracking related messages across services.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// The tenant ID for multi-tenant scenarios.
    /// </summary>
    public Guid TenantId { get; init; }

    /// <summary>
    /// The session ID for tracking related operations.
    /// </summary>
    public Guid SessionId { get; init; }

    /// <summary>
    /// The user who initiated this message.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Optional metadata/headers for the message.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Base class for command messages (intent to change state).
/// </summary>
public abstract record CommandMessage : MessageBase;

/// <summary>
/// Base class for event messages (notification that something happened).
/// </summary>
public abstract record EventMessage : MessageBase;

/// <summary>
/// Base class for query messages (request for data).
/// </summary>
public abstract record QueryMessage : MessageBase;
