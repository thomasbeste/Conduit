using Conduit.Mediator;
using Conduit.Messaging.Bridge;
using Conduit.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of IMessagePublisher.
/// Publishes to type-based fanout exchanges. When an <see cref="IPipelineContext"/> is available
/// in the current DI scope, automatically propagates baggage and causality chain to message headers.
/// </summary>
public sealed class RabbitMqPublisher(
    IChannel channel,
    IServiceProvider serviceProvider,
    ILogger logger) : IMessagePublisher, IAsyncDisposable
{
    /// <summary>
    /// Tracks exchanges that have been declared on this channel.
    /// </summary>
    private readonly HashSet<string> _declaredExchanges = [];

    public async Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var exchangeName = MessageSerializer.GetExchangeName(typeof(TMessage));
        await EnsureExchangeDeclaredAsync(exchangeName, cancellationToken);

        var contextHeaders = ExtractContextHeaders();
        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name, contextHeaders);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = contextHeaders?.GetValueOrDefault("conduit.correlation-id"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(exchangeName, routingKey: "", mandatory: false, properties, body, cancellationToken);

        logger.LogDebug("Published {MessageType} to exchange {Exchange}", typeof(TMessage).Name, exchangeName);
    }

    public async Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var exchangeName = MessageSerializer.GetExchangeName(typeof(TMessage));
        await EnsureExchangeDeclaredAsync(exchangeName, "topic", cancellationToken);

        var contextHeaders = ExtractContextHeaders();
        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name, contextHeaders);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = contextHeaders?.GetValueOrDefault("conduit.correlation-id"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(exchangeName, routingKey: topic, mandatory: false, properties, body, cancellationToken);

        logger.LogDebug("Published {MessageType} to exchange {Exchange} with topic {Topic}",
            typeof(TMessage).Name, exchangeName, topic);
    }

    public async Task SendAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var contextHeaders = ExtractContextHeaders();
        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name, contextHeaders);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = contextHeaders?.GetValueOrDefault("conduit.correlation-id"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        // Send directly to default exchange with queue name as routing key
        await channel.BasicPublishAsync("", routingKey: queueName, mandatory: false, properties, body, cancellationToken);

        logger.LogDebug("Sent {MessageType} to queue {Queue}", typeof(TMessage).Name, queueName);
    }

    /// <summary>
    /// Attempts to resolve IPipelineContext from the current DI scope and extract headers.
    /// Returns null if no pipeline context is available (graceful degradation).
    /// </summary>
    private Dictionary<string, string>? ExtractContextHeaders()
    {
        try
        {
            var pipelineContext = serviceProvider.GetService<IPipelineContext>();
            return pipelineContext is not null ? PipelineContextBridge.ExtractHeaders(pipelineContext) : null;
        }
        catch
        {
            // Gracefully handle cases where we're outside a DI scope
            return null;
        }
    }

    private async Task EnsureExchangeDeclaredAsync(string exchangeName, CancellationToken cancellationToken)
    {
        await EnsureExchangeDeclaredAsync(exchangeName, "fanout", cancellationToken);
    }

    private async Task EnsureExchangeDeclaredAsync(string exchangeName, string type, CancellationToken cancellationToken)
    {
        if (_declaredExchanges.Add(exchangeName))
        {
            await channel.ExchangeDeclareAsync(exchangeName, type, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await channel.CloseAsync();
        channel.Dispose();
    }
}
