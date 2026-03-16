namespace Conduit.Messaging;

/// <summary>
/// Abstraction for publishing messages to a message broker.
/// Implementations handle serialization, routing, and transport-specific concerns.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes a message to all subscribers.
    /// Uses message type to determine routing.
    /// </summary>
    Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class;

    /// <summary>
    /// Publishes a message to all subscribers with optional context headers for cross-process propagation.
    /// </summary>
    Task PublishAsync<TMessage>(TMessage message, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
        => PublishAsync(message, cancellationToken);

    /// <summary>
    /// Publishes a message to a specific topic/exchange.
    /// </summary>
    Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
        where TMessage : class;

    /// <summary>
    /// Publishes a message to a specific topic/exchange with optional context headers.
    /// </summary>
    Task PublishAsync<TMessage>(TMessage message, string topic, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
        => PublishAsync(message, topic, cancellationToken);

    /// <summary>
    /// Sends a message to a specific queue/endpoint (point-to-point).
    /// Only one consumer will receive the message.
    /// </summary>
    Task SendAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        where TMessage : class;

    /// <summary>
    /// Sends a message to a specific queue/endpoint with optional context headers.
    /// </summary>
    Task SendAsync<TMessage>(TMessage message, string queueName, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
        => SendAsync(message, queueName, cancellationToken);
}
