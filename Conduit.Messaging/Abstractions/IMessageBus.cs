namespace Conduit.Messaging;

/// <summary>
/// High-level abstraction combining publish and subscribe capabilities.
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// Gets the message publisher.
    /// </summary>
    IMessagePublisher Publisher { get; }

    /// <summary>
    /// Starts the message bus and begins consuming messages.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the message bus and disconnects from the broker.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the health status of the message bus connection.
    /// </summary>
    MessageBusHealth GetHealth();
}

/// <summary>
/// Health status of the message bus.
/// </summary>
public sealed class MessageBusHealth
{
    /// <summary>
    /// Whether the message bus is connected and healthy.
    /// </summary>
    public required bool IsHealthy { get; init; }

    /// <summary>
    /// Descriptive status message.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Additional diagnostic details.
    /// </summary>
    public Dictionary<string, object>? Details { get; init; }
}
