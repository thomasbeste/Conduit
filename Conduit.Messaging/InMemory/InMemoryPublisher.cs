namespace Conduit.Messaging.InMemory;

/// <summary>
/// In-memory publisher that routes messages through InMemoryMessageBus.
/// </summary>
public sealed class InMemoryPublisher(InMemoryMessageBus bus) : IMessagePublisher
{
    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
        => bus.DispatchAsync(message, cancellationToken);

    public Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
        where TMessage : class
        => bus.DispatchAsync(message, cancellationToken);

    public Task SendAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        where TMessage : class
        => bus.DispatchAsync(message, cancellationToken);
}
