using Conduit.Mediator;

namespace Conduit.Messaging.Bridge;

/// <summary>
/// Decorator that automatically extracts pipeline context (baggage, causality, correlation)
/// into message headers on every publish/send call. Ensures cross-process context propagation
/// without requiring callers to manually extract headers.
///
/// Uses lazy publisher resolution from IMessageBus to avoid accessing bus.Publisher
/// before the hosted service has called StartAsync.
/// </summary>
public sealed class ContextPropagatingPublisher(IMessageBus bus) : IMessagePublisher
{
    private IMessagePublisher Inner => bus.Publisher;

    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
        => Inner.PublishAsync(message, ExtractHeaders(), cancellationToken);

    public Task PublishAsync<TMessage>(TMessage message, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
        => Inner.PublishAsync(message, MergeHeaders(contextHeaders), cancellationToken);

    public Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
        where TMessage : class
        => Inner.PublishAsync(message, topic, ExtractHeaders(), cancellationToken);

    public Task PublishAsync<TMessage>(TMessage message, string topic, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
        => Inner.PublishAsync(message, topic, MergeHeaders(contextHeaders), cancellationToken);

    public Task SendAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        where TMessage : class
        => Inner.SendAsync(message, queueName, ExtractHeaders(), cancellationToken);

    public Task SendAsync<TMessage>(TMessage message, string queueName, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
        => Inner.SendAsync(message, queueName, MergeHeaders(contextHeaders), cancellationToken);

    private static Dictionary<string, string> ExtractHeaders()
    {
        var context = PipelineContext.Current;
        return context is not null
            ? PipelineContextBridge.ExtractHeaders(context)
            : new Dictionary<string, string>();
    }

    private static Dictionary<string, string> MergeHeaders(IReadOnlyDictionary<string, string>? explicitHeaders)
    {
        var headers = ExtractHeaders();

        if (explicitHeaders is not null)
        {
            foreach (var kv in explicitHeaders)
                headers[kv.Key] = kv.Value;
        }

        return headers;
    }
}
