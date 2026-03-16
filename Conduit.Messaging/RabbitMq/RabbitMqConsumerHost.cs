using System.Text;
using Conduit.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// Hosts a single consumer channel, declares queue/exchange/binding, and dispatches messages.
/// </summary>
public sealed class RabbitMqConsumerHost
{
    private readonly IChannel _channel;
    private readonly ConsumerRegistration _registration;
    private readonly string _serviceName;
    private readonly RabbitMqSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private string? _consumerTag;

    public RabbitMqConsumerHost(
        IChannel channel,
        ConsumerRegistration registration,
        string serviceName,
        RabbitMqSettings settings,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        _channel = channel;
        _registration = registration;
        _serviceName = serviceName;
        _settings = settings;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var exchangeName = MessageSerializer.GetExchangeName(_registration.MessageType);
        var queueName = MessageSerializer.GetQueueName(_serviceName, _registration.ConsumerType);
        var dlxExchange = $"{exchangeName}.dlx";
        var dlqQueue = $"{queueName}.dlq";

        // Declare dead-letter exchange and queue
        await _channel.ExchangeDeclareAsync(dlxExchange, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(dlqQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await _channel.QueueBindAsync(dlqQueue, dlxExchange, "", cancellationToken: cancellationToken);

        // Declare message type exchange
        await _channel.ExchangeDeclareAsync(exchangeName, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);

        // Declare consumer queue with dead-letter configuration
        var queueArgs = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = dlxExchange,
            ["x-dead-letter-routing-key"] = dlqQueue
        };

        await _channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false,
            arguments: queueArgs, cancellationToken: cancellationToken);
        await _channel.QueueBindAsync(queueName, exchangeName, "", cancellationToken: cancellationToken);

        // Start consuming
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        _consumerTag = await _channel.BasicConsumeAsync(queueName, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Consumer {ConsumerType} started on queue {Queue} bound to {Exchange}",
            _registration.ConsumerType.Name, queueName, exchangeName);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var deliveryCount = GetDeliveryCount(ea);

        try
        {
            var (message, envelope) = MessageSerializer.Deserialize(ea.Body, _registration.MessageType);

            if (message == null)
            {
                _logger.LogWarning("Deserialized null message from {Exchange}, nacking without requeue",
                    ea.Exchange);
                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            var context = new MessageContext
            {
                MessageId = Guid.TryParse(ea.BasicProperties.MessageId, out var mid) ? mid : Guid.NewGuid(),
                CorrelationId = ea.BasicProperties.CorrelationId,
                SentTime = envelope.Timestamp != default ? envelope.Timestamp : null,
                SourceAddress = ea.Exchange,
                DestinationAddress = ea.RoutingKey,
                DeliveryCount = deliveryCount,
                Headers = ParseHeaders(ea.BasicProperties.Headers)
            };

            // Resolve consumer from DI and dispatch
            await using var scope = _serviceProvider.CreateAsyncScope();
            var consumerInstance = scope.ServiceProvider.GetRequiredService(_registration.ConsumerType);

            // Call ConsumeAsync via the IMessageConsumer<T> interface
            var consumerInterface = typeof(IMessageConsumer<>).MakeGenericType(_registration.MessageType);
            var consumeMethod = consumerInterface.GetMethod(nameof(IMessageConsumer<object>.ConsumeAsync))!;

            await (Task)consumeMethod.Invoke(consumerInstance, [message, context, CancellationToken.None])!;

            await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consuming message from {Exchange} (delivery #{Count})",
                ea.Exchange, deliveryCount);

            // Requeue if under retry limit, otherwise dead-letter
            var shouldRequeue = deliveryCount < _settings.RetryCount;
            await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: shouldRequeue);

            if (!shouldRequeue)
            {
                _logger.LogWarning(
                    "Message from {Exchange} exceeded retry limit ({RetryCount}), sent to DLQ",
                    ea.Exchange, _settings.RetryCount);
            }
        }
    }

    private static int GetDeliveryCount(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties.Headers?.TryGetValue("x-delivery-count", out var count) == true)
        {
            return count is int i ? i : 0;
        }
        return ea.Redelivered ? 1 : 0;
    }

    private static IReadOnlyDictionary<string, object>? ParseHeaders(IDictionary<string, object?>? headers)
    {
        if (headers == null || headers.Count == 0) return null;

        var result = new Dictionary<string, object>();
        foreach (var (key, value) in headers)
        {
            if (value is byte[] bytes)
                result[key] = Encoding.UTF8.GetString(bytes);
            else if (value != null)
                result[key] = value;
        }
        return result;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_consumerTag != null)
        {
            await _channel.BasicCancelAsync(_consumerTag, cancellationToken: cancellationToken);
        }
        await _channel.CloseAsync(cancellationToken);
        _channel.Dispose();
    }
}
