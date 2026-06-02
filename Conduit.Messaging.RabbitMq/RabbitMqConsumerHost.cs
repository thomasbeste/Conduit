using System.Diagnostics.Metrics;
using System.Text;
using Conduit.Mediator;
using Conduit.Messaging.Bridge;
using Conduit.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// Hosts a single RabbitMQ consumer as an <see cref="ISupervisedConsumer"/>: it
/// declares queue/exchange/binding, dispatches messages, and exposes the two
/// primitives the transport-agnostic <see cref="ConsumerSupervisor"/> drives —
/// <see cref="IsHealthy"/> and <see cref="EnsureRunningAsync"/>.
///
/// Durability is NOT delegated to the client library's best-effort automatic
/// recovery (which, after a long enough broker crashloop, can stop firing
/// recovery events and strand the consumer until the process restarts — the
/// 2026-06-02 incident). Instead the host can rebuild from ANY dead state on
/// demand: it resolves a live <see cref="IConnection"/> through the
/// connection provider (which recreates the underlying connection when it has
/// died), then rebuilds its channel + bindings + consumer. The supervisor polls
/// <see cref="IsHealthy"/> and calls <see cref="EnsureRunningAsync"/> whenever it
/// is false — forever — so any outage heals the moment the broker is reachable.
/// </summary>
public sealed class RabbitMqConsumerHost : ISupervisedConsumer
{
    internal static readonly Meter Meter = new("Conduit.Messaging.RabbitMq");

    private static readonly Counter<long> IdentitySignatureMismatchCounter =
        Meter.CreateCounter<long>(
            "messaging.rmq.identity_signature_mismatch",
            unit: "{message}",
            description: "Messages dead-lettered because their identity-baggage HMAC did not verify.");

    private static readonly Counter<long> HydrationErrorCounter =
        Meter.CreateCounter<long>(
            "messaging.rmq.hydration_error",
            unit: "{message}",
            description: "Messages dead-lettered because pipeline-context hydration failed for a non-signature reason.");

    private readonly Func<CancellationToken, Task<IConnection>> _connectionProvider;
    private readonly ConsumerRegistration _registration;
    private readonly string _serviceName;
    private readonly RabbitMqSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly Func<Action<object, Type>?> _getOnMessageConsumed;

    private readonly string _exchangeName;
    private readonly string _queueName;
    private readonly string _dlxExchange;
    private readonly string _dlqQueue;

    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private IChannel? _channel;
    private string? _consumerTag;
    private volatile bool _disposed;

    public RabbitMqConsumerHost(
        Func<CancellationToken, Task<IConnection>> connectionProvider,
        ConsumerRegistration registration,
        string serviceName,
        RabbitMqSettings settings,
        IServiceProvider serviceProvider,
        ILogger logger,
        Func<Action<object, Type>?>? getOnMessageConsumed = null)
    {
        _connectionProvider = connectionProvider;
        _registration = registration;
        _serviceName = serviceName;
        _settings = settings;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _getOnMessageConsumed = getOnMessageConsumed ?? (() => null);

        _exchangeName = MessageSerializer.GetExchangeName(registration.MessageType);
        _queueName = MessageSerializer.GetQueueName(serviceName, registration.ConsumerType);
        _dlxExchange = $"{_exchangeName}.dlx";
        _dlqQueue = $"{_queueName}.dlq";
    }

    public string Name => _queueName;

    /// <summary>
    /// Live iff we hold an open channel with an active consumer. A closed channel
    /// (broker forced-close, network blip) or a never-built one both read false,
    /// which is exactly the signal the supervisor rebuilds on. Never throws.
    /// </summary>
    public bool IsHealthy => !_disposed && _channel is { IsOpen: true } && _consumerTag is not null;

    /// <summary>
    /// Idempotent rebuild-to-running. Resolves a live connection (recreated by the
    /// provider if the current one is dead), then rebuilds the channel + topology
    /// + consumer if they are not already healthy. A no-op when already healthy.
    /// </summary>
    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        await _rebuildLock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsHealthy) return;

            // Drop a dead channel before building a new one. The old consumer is
            // tied to it; disposing detaches everything cleanly.
            if (_channel is not null)
            {
                try { _channel.Dispose(); } catch { /* already dead */ }
                _channel = null;
                _consumerTag = null;
            }

            // Resolve a live connection. The provider recreates the underlying
            // IConnection if it has permanently died — this is the step that the
            // old event-driven design could never reach.
            var connection = await _connectionProvider(cancellationToken);

            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await channel.BasicQosAsync(0, _settings.PrefetchCount, false, cancellationToken);

            // Dead-letter exchange + queue.
            await channel.ExchangeDeclareAsync(_dlxExchange, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);
            await channel.QueueDeclareAsync(_dlqQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(_dlqQueue, _dlxExchange, "", cancellationToken: cancellationToken);

            // Message-type exchange.
            await channel.ExchangeDeclareAsync(_exchangeName, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);

            var queueArgs = new Dictionary<string, object?>
            {
                // Consumer queues MUST be quorum: only quorum stamps the
                // x-delivery-count header GetDeliveryCount reads to enforce the
                // retry cap. x-delivery-limit is the broker-native poison backstop.
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = _settings.RetryCount + 1,
                ["x-dead-letter-exchange"] = _dlxExchange,
                ["x-dead-letter-routing-key"] = _dlqQueue
            };
            await channel.QueueDeclareAsync(_queueName, durable: true, exclusive: false, autoDelete: false,
                arguments: queueArgs, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(_queueName, _exchangeName, "", cancellationToken: cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnMessageReceivedAsync;
            var consumerTag = await channel.BasicConsumeAsync(_queueName, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);

            _channel = channel;
            _consumerTag = consumerTag;

            _logger.LogInformation(
                "Consumer {ConsumerType} consuming on queue {Queue} bound to {Exchange}",
                _registration.ConsumerType.Name, _queueName, _exchangeName);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        // Capture the channel locally so a concurrent rebuild can't null it mid-handler.
        var channel = _channel;
        if (channel is null) return;

        var deliveryCount = GetDeliveryCount(ea);

        try
        {
            var (message, envelope) = MessageSerializer.Deserialize(ea.Body, _registration.MessageType);

            if (message == null)
            {
                _logger.LogWarning("Deserialized null message from {Exchange}, nacking without requeue", ea.Exchange);
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

            await using var scope = _serviceProvider.CreateAsyncScope();

            var pipelineContext = scope.ServiceProvider.GetService<IPipelineContext>();
            if (pipelineContext is not null)
            {
                var signingKey = scope.ServiceProvider.GetService<IMessagingSigningKey>();
                try
                {
                    PipelineContextBridge.HydrateContext(pipelineContext, context, signingKey);
                }
                catch (IdentitySignatureMismatchException ex)
                {
                    IdentitySignatureMismatchCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("exchange", ea.Exchange),
                        new KeyValuePair<string, object?>("service", _serviceName));
                    _logger.LogError(
                        "Dead-lettering RMQ message {MessageId} from {Exchange}: identity-signature-invalid ({Reason})",
                        ea.BasicProperties.MessageId, ea.Exchange, ex.Message);
                    try { await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false); }
                    catch (AlreadyClosedException) { }
                    return;
                }
                catch (Exception ex)
                {
                    HydrationErrorCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("exchange", ea.Exchange),
                        new KeyValuePair<string, object?>("service", _serviceName));
                    _logger.LogError(ex,
                        "Dead-lettering RMQ message {MessageId} from {Exchange}: pipeline-context hydration failed",
                        ea.BasicProperties.MessageId, ea.Exchange);
                    try { await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false); }
                    catch (AlreadyClosedException) { }
                    return;
                }

                if (pipelineContext is PipelineContext concrete)
                    PipelineContext.SetCurrent(concrete);
            }

            var consumerInstance = scope.ServiceProvider.GetRequiredService(_registration.ConsumerType);
            await _registration.GetDispatcher().DispatchAsync(consumerInstance, message, context, CancellationToken.None);

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            _getOnMessageConsumed()?.Invoke(message, _registration.MessageType);
        }
        catch (AlreadyClosedException)
        {
            // Channel died mid-dispatch. The supervisor's next health probe sees
            // a closed channel and rebuilds; the broker redelivers the unacked
            // message to the fresh consumer. Nothing to do here.
            _logger.LogWarning(
                "Consumer {ConsumerType}: channel closed during dispatch — supervisor will rebuild",
                _registration.ConsumerType.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consuming message from {Exchange} (delivery #{Count})", ea.Exchange, deliveryCount);

            var shouldRequeue = deliveryCount < _settings.RetryCount;
            try
            {
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: shouldRequeue);
            }
            catch (AlreadyClosedException) { }

            if (!shouldRequeue)
            {
                _logger.LogWarning("Message from {Exchange} exceeded retry limit ({RetryCount}), sent to DLQ",
                    ea.Exchange, _settings.RetryCount);
            }
        }
    }

    private static int GetDeliveryCount(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties.Headers?.TryGetValue("x-delivery-count", out var count) == true)
        {
            return count switch
            {
                long l => (int)l,
                int i => i,
                _ => 0
            };
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

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _rebuildLock.WaitAsync();
        try
        {
            if (_channel is not null)
            {
                try
                {
                    if (_channel.IsOpen)
                    {
                        if (_consumerTag is not null)
                            await _channel.BasicCancelAsync(_consumerTag);
                        await _channel.CloseAsync();
                    }
                }
                catch (ObjectDisposedException) { }
                catch (AlreadyClosedException) { }
                finally
                {
                    _channel.Dispose();
                    _channel = null;
                    _consumerTag = null;
                }
            }
        }
        finally
        {
            _rebuildLock.Release();
            _rebuildLock.Dispose();
        }
    }
}
