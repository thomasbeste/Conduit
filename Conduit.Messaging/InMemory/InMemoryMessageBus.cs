using System.Collections.Concurrent;
using Conduit.Mediator;
using Conduit.Messaging.Bridge;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Messaging.InMemory;

/// <summary>
/// In-memory message bus for testing. Publish routes directly to registered consumers in-process.
/// </summary>
public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly ConcurrentBag<PublishedMessageRecord> _published = [];
    private readonly ConcurrentBag<ConsumedMessageRecord> _consumed = [];
    private readonly List<ConsumerBinding> _bindings = [];
    private readonly IServiceProvider _serviceProvider;
    private bool _started;

    public InMemoryMessageBus(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        Publisher = new InMemoryPublisher(this);
    }

    public IMessagePublisher Publisher { get; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _started = false;
        return Task.CompletedTask;
    }

    public MessageBusHealth GetHealth() => new()
    {
        IsHealthy = _started,
        Status = _started ? "Running" : "Stopped"
    };

    /// <summary>
    /// Registers a consumer binding for in-memory dispatch.
    /// </summary>
    internal void AddBinding(Type messageType, Type consumerType)
    {
        _bindings.Add(new ConsumerBinding(messageType, consumerType));
    }

    /// <summary>
    /// Dispatches a published message to registered consumers.
    /// </summary>
    internal async Task DispatchAsync(object message, CancellationToken cancellationToken)
    {
        var messageType = message.GetType();
        _published.Add(new PublishedMessageRecord(message, messageType, DateTime.UtcNow));

        // Extract context headers from the publishing scope's pipeline context
        Dictionary<string, string>? contextHeaders = null;
        var publisherContext = _serviceProvider.GetService<IPipelineContext>();
        if (publisherContext is not null)
        {
            contextHeaders = PipelineContextBridge.ExtractHeaders(publisherContext);
        }

        var context = new MessageContext
        {
            MessageId = Guid.NewGuid(),
            SentTime = DateTime.UtcNow,
            Headers = contextHeaders?.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
        };

        foreach (var binding in _bindings.Where(b => b.MessageType.IsAssignableFrom(messageType)))
        {
            await using var scope = _serviceProvider.CreateAsyncScope();

            // Hydrate the consumer scope's pipeline context with cross-process state
            var consumerPipelineContext = scope.ServiceProvider.GetService<IPipelineContext>();
            if (consumerPipelineContext is not null)
            {
                PipelineContextBridge.HydrateContext(consumerPipelineContext, context);
            }

            var consumer = scope.ServiceProvider.GetRequiredService(binding.ConsumerType);

            var consumerInterface = typeof(IMessageConsumer<>).MakeGenericType(binding.MessageType);
            var consumeMethod = consumerInterface.GetMethod(nameof(IMessageConsumer<object>.ConsumeAsync))!;

            await (Task)consumeMethod.Invoke(consumer, [message, context, cancellationToken])!;

            _consumed.Add(new ConsumedMessageRecord(message, messageType, binding.ConsumerType, DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Gets all published messages of type T.
    /// </summary>
    public IEnumerable<T> GetPublished<T>() where T : class
        => _published.Where(m => m.Message is T).Select(m => (T)m.Message);

    /// <summary>
    /// Gets all published messages of type T after a specific time.
    /// </summary>
    public IEnumerable<T> GetPublishedAfter<T>(DateTime after) where T : class
        => _published.Where(m => m.Message is T && m.Timestamp > after).Select(m => (T)m.Message);

    /// <summary>
    /// Gets all consumed messages of type T.
    /// </summary>
    public IEnumerable<T> GetConsumed<T>() where T : class
        => _consumed.Where(m => m.Message is T).Select(m => (T)m.Message);

    /// <summary>
    /// Gets all consumed messages of type T after a specific time.
    /// </summary>
    public IEnumerable<T> GetConsumedAfter<T>(DateTime after) where T : class
        => _consumed.Where(m => m.Message is T && m.Timestamp > after).Select(m => (T)m.Message);

    /// <summary>
    /// Waits for a message of type T matching the predicate to be consumed.
    /// </summary>
    public async Task<T> WaitForConsume<T>(Func<T, bool>? predicate = null, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        predicate ??= _ => true;

        while (DateTime.UtcNow < deadline)
        {
            var match = _consumed
                .Where(m => m.Message is T)
                .Select(m => (T)m.Message)
                .FirstOrDefault(predicate);

            if (match != null) return match;
            await Task.Delay(100);
        }

        throw new TimeoutException($"No consumed message of type {typeof(T).Name} matched within timeout");
    }

    /// <summary>
    /// Clears all captured messages.
    /// </summary>
    public void Clear()
    {
        _published.Clear();
        _consumed.Clear();
    }

    public IReadOnlyCollection<PublishedMessageRecord> Published => _published.ToArray();
    public IReadOnlyCollection<ConsumedMessageRecord> Consumed => _consumed.ToArray();
}

public record PublishedMessageRecord(object Message, Type MessageType, DateTime Timestamp);
public record ConsumedMessageRecord(object Message, Type MessageType, Type ConsumerType, DateTime Timestamp);

internal record ConsumerBinding(Type MessageType, Type ConsumerType);
