using Conduit.Messaging.Serialization;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of IMessagePublisher.
/// Publishes to type-based fanout exchanges.
/// </summary>
public sealed class RabbitMqPublisher(
    IChannel channel,
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

        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
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

        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(exchangeName, routingKey: topic, mandatory: false, properties, body, cancellationToken);

        logger.LogDebug("Published {MessageType} to exchange {Exchange} with topic {Topic}",
            typeof(TMessage).Name, exchangeName, topic);
    }

    public async Task SendAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        // Send directly to default exchange with queue name as routing key
        await channel.BasicPublishAsync("", routingKey: queueName, mandatory: false, properties, body, cancellationToken);

        logger.LogDebug("Sent {MessageType} to queue {Queue}", typeof(TMessage).Name, queueName);
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
