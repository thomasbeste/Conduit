using System.Text;
using Conduit.Mediator;
using Conduit.Messaging.Bridge;
using Conduit.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// Hosts a single consumer channel, declares queue/exchange/binding, and dispatches messages.
/// </summary>
public sealed class RabbitMqConsumerHost(
    IChannel channel,
    ConsumerRegistration registration,
    string serviceName,
    RabbitMqSettings settings,
    IServiceProvider serviceProvider,
    ILogger logger,
    Func<Action<object, Type>?>? getOnMessageConsumed = null)
{
    private readonly Func<Action<object, Type>?> _getOnMessageConsumed = getOnMessageConsumed ?? (() => null);
    private string? _consumerTag;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var exchangeName = MessageSerializer.GetExchangeName(registration.MessageType);
        var queueName = MessageSerializer.GetQueueName(serviceName, registration.ConsumerType);
        var dlxExchange = $"{exchangeName}.dlx";
        var dlqQueue = $"{queueName}.dlq";

        // Declare dead-letter exchange and queue
        await channel.ExchangeDeclareAsync(dlxExchange, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(dlqQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(dlqQueue, dlxExchange, "", cancellationToken: cancellationToken);

        // Declare message type exchange
        await channel.ExchangeDeclareAsync(exchangeName, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);

        // Declare consumer queue with dead-letter configuration
        var queueArgs = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = dlxExchange,
            ["x-dead-letter-routing-key"] = dlqQueue
        };

        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false,
            arguments: queueArgs, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queueName, exchangeName, "", cancellationToken: cancellationToken);

        // Start consuming
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        _consumerTag = await channel.BasicConsumeAsync(queueName, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Consumer {ConsumerType} started on queue {Queue} bound to {Exchange}",
            registration.ConsumerType.Name, queueName, exchangeName);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var deliveryCount = GetDeliveryCount(ea);

        try
        {
            var (message, envelope) = MessageSerializer.Deserialize(ea.Body, registration.MessageType);

            if (message == null)
            {
                logger.LogWarning("Deserialized null message from {Exchange}, nacking without requeue",
                    ea.Exchange);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
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
            await using var scope = serviceProvider.CreateAsyncScope();

            // Hydrate pipeline context with cross-process state (baggage, causality) if available
            var pipelineContext = scope.ServiceProvider.GetService<IPipelineContext>();
            if (pipelineContext is not null)
            {
                PipelineContextBridge.HydrateContext(pipelineContext, context);

                // Set ambient context so consumers can access it via PipelineContext.Current
                if (pipelineContext is PipelineContext concrete)
                    PipelineContext.SetCurrent(concrete);
            }

            var consumerInstance = scope.ServiceProvider.GetRequiredService(registration.ConsumerType);
            await registration.GetDispatcher().DispatchAsync(consumerInstance, message, context, CancellationToken.None);

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            _getOnMessageConsumed()?.Invoke(message, registration.MessageType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error consuming message from {Exchange} (delivery #{Count})",
                ea.Exchange, deliveryCount);

            // Requeue if under retry limit, otherwise dead-letter
            var shouldRequeue = deliveryCount < settings.RetryCount;
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: shouldRequeue);

            if (!shouldRequeue)
            {
                logger.LogWarning(
                    "Message from {Exchange} exceeded retry limit ({RetryCount}), sent to DLQ",
                    ea.Exchange, settings.RetryCount);
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

    private static IReadOnlyDictionary<string, string>? ParseHeaders(IDictionary<string, object?>? headers)
    {
        if (headers == null || headers.Count == 0) return null;

        var result = new Dictionary<string, string>();
        foreach (var (key, value) in headers)
        {
            if (value is byte[] bytes)
                result[key] = Encoding.UTF8.GetString(bytes);
            else if (value != null)
                result[key] = value.ToString()!;
        }
        return result;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_consumerTag != null)
        {
            await channel.BasicCancelAsync(_consumerTag, cancellationToken: cancellationToken);
        }
        await channel.CloseAsync(cancellationToken);
        channel.Dispose();
    }
}
