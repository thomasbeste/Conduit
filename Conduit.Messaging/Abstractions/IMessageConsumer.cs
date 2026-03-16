namespace Conduit.Messaging;

/// <summary>
/// Base interface for message consumers.
/// Implement this interface to handle messages from the message broker.
/// </summary>
/// <typeparam name="TMessage">The message type this consumer handles</typeparam>
public interface IMessageConsumer<in TMessage>
    where TMessage : class
{
    /// <summary>
    /// Handles the consumed message.
    /// </summary>
    Task ConsumeAsync(TMessage message, MessageContext context, CancellationToken cancellationToken = default);
}
