using System.Collections.Concurrent;

namespace Conduit.Messaging;

/// <summary>
/// Registration of a consumer type and its message type.
/// </summary>
public sealed class ConsumerRegistration
{
    public required Type ConsumerType { get; init; }
    public required Type MessageType { get; init; }

    /// <summary>
    /// Gets or creates a cached dispatcher for this registration's message type.
    /// Eliminates per-message reflection (MakeGenericType/GetMethod/Invoke).
    /// </summary>
    public ConsumerDispatcher GetDispatcher() => CreateDispatcher(MessageType);

    /// <summary>
    /// Creates or retrieves a cached dispatcher for the given message type.
    /// </summary>
    public static ConsumerDispatcher CreateDispatcher(Type messageType)
        => DispatcherCache.GetOrAdd(messageType, static t =>
        {
            var dispatcherType = typeof(ConsumerDispatcher<>).MakeGenericType(t);
            return (ConsumerDispatcher)Activator.CreateInstance(dispatcherType)!;
        });

    private static readonly ConcurrentDictionary<Type, ConsumerDispatcher> DispatcherCache = new();
}

/// <summary>
/// Abstract base for cached consumer dispatchers. Created once per message type.
/// </summary>
public abstract class ConsumerDispatcher
{
    public abstract Task DispatchAsync(object consumer, object message, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Strongly-typed consumer dispatcher that calls <see cref="IMessageConsumer{TMessage}.ConsumeAsync"/> directly.
/// </summary>
public sealed class ConsumerDispatcher<TMessage> : ConsumerDispatcher where TMessage : class
{
    public override Task DispatchAsync(object consumer, object message, MessageContext context, CancellationToken cancellationToken)
        => ((IMessageConsumer<TMessage>)consumer).ConsumeAsync((TMessage)message, context, cancellationToken);
}
