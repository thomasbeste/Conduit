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
/// Hosts a single consumer channel, declares queue/exchange/binding, dispatches messages,
/// and self-heals across broker disconnects / channel shutdowns.
///
/// Self-healing contract (issue #687-era audit incident, 2026-05-16):
///   - The host owns its own IChannel, created lazily from the long-lived
///     <see cref="IConnection"/> the bus owns.
///   - When the broker initiates a shutdown (code 320 CONNECTION_FORCED on
///     pod restart) the channel's ChannelShutdownAsync event fires; the
///     host catches it, drops the dead channel, waits for the connection
///     to come back, and rebuilds bindings + consumer from scratch.
///   - The IConnection has AutomaticRecoveryEnabled + TopologyRecoveryEnabled
///     (configured on the factory in <see cref="RabbitMqMessageBus"/>) so the
///     connection itself comes back. We additionally hook RecoverySucceededAsync
///     as a redundant signal — belt and braces.
/// </summary>
public sealed class RabbitMqConsumerHost(
    IConnection connection,
    ConsumerRegistration registration,
    string serviceName,
    RabbitMqSettings settings,
    IServiceProvider serviceProvider,
    ILogger logger,
    Func<Action<object, Type>?>? getOnMessageConsumed = null)
{
    // Single Meter for the RMQ transport. Mirrors the ASB host: one counter
    // for signed-baggage mismatches (Attacker D — broker access on k3s/k3d),
    // one for any other hydration failure (deploy bug — malformed baggage,
    // config error). Tag set kept low-cardinality (exchange + service only —
    // never message id or baggage value).
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

    private readonly Func<Action<object, Type>?> _getOnMessageConsumed = getOnMessageConsumed ?? (() => null);
    private IChannel? _channel;
    private string? _consumerTag;
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private CancellationTokenSource? _hostCts;
    private bool _stopping;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _hostCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Belt + braces: hook the connection's recovery callback so we rebuild
        // the consumer end-to-end after a broker bounce, even if the channel
        // shutdown event raced ahead of us.
        connection.RecoverySucceededAsync += OnConnectionRecoveryAsync;

        await OpenAndBindAsync(_hostCts.Token);
    }

    private async Task OpenAndBindAsync(CancellationToken cancellationToken)
    {
        var exchangeName = MessageSerializer.GetExchangeName(registration.MessageType);
        var queueName = MessageSerializer.GetQueueName(serviceName, registration.ConsumerType);
        var dlxExchange = $"{exchangeName}.dlx";
        var dlqQueue = $"{queueName}.dlq";

        // Open a fresh channel. Retried-with-backoff so a publisher attempt
        // during the same broker hiccup doesn't see "consumer dead" for long.
        _channel = await CreateChannelWithRetryAsync(cancellationToken);

        // The channel-shutdown handler is what kicks self-healing for the
        // common case (broker forced close, network blip).
        _channel.ChannelShutdownAsync += OnChannelShutdownAsync;

        await _channel.BasicQosAsync(0, settings.PrefetchCount, false, cancellationToken);

        // Dead-letter exchange + queue
        await _channel.ExchangeDeclareAsync(dlxExchange, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(dlqQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await _channel.QueueBindAsync(dlqQueue, dlxExchange, "", cancellationToken: cancellationToken);

        // Message type exchange
        await _channel.ExchangeDeclareAsync(exchangeName, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken);

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

        logger.LogInformation(
            "Consumer {ConsumerType} started on queue {Queue} bound to {Exchange}",
            registration.ConsumerType.Name, queueName, exchangeName);
    }

    private async Task<IChannel> CreateChannelWithRetryAsync(CancellationToken cancellationToken)
    {
        // The connection may be mid-recovery. Bounded backoff so a dead
        // broker doesn't hang the host indefinitely, but generous enough
        // to cover a typical k8s pod restart (~30-60s).
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = Math.Min(attempt * 2, 30);
                logger.LogWarning(
                    "Consumer {ConsumerType} channel-create attempt {Attempt}/{Max} failed ({Reason}), retrying in {Delay}s",
                    registration.ConsumerType.Name, attempt, maxAttempts, ex.GetType().Name, delay);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken); }
                catch (OperationCanceledException) { throw; }
            }
        }
    }

    private async Task OnChannelShutdownAsync(object sender, ShutdownEventArgs args)
    {
        if (_stopping) return;
        logger.LogWarning(
            "Consumer {ConsumerType} channel shutdown ({Code} {Text}) — rebuilding",
            registration.ConsumerType.Name, args.ReplyCode, args.ReplyText);
        await RestartAsync();
    }

    private async Task OnConnectionRecoveryAsync(object sender, AsyncEventArgs args)
    {
        if (_stopping) return;
        // The channel-shutdown handler usually wins this race; this branch
        // is the fallback for cases where channel events were lost.
        if (_channel is { IsOpen: true }) return;
        logger.LogInformation(
            "Consumer {ConsumerType} reacting to connection recovery — rebuilding",
            registration.ConsumerType.Name);
        await RestartAsync();
    }

    private async Task RestartAsync()
    {
        // Serialise restarts so a channel-shutdown and a connection-recovery
        // event firing back-to-back don't double-open the consumer.
        if (!await _restartLock.WaitAsync(0))
        {
            // Another restart is in flight; let it do the work.
            return;
        }
        try
        {
            if (_stopping || _hostCts is null) return;

            // Detach the old handler so the about-to-be-disposed channel
            // can't re-enter Restart on its own shutdown.
            if (_channel is not null)
            {
                try { _channel.ChannelShutdownAsync -= OnChannelShutdownAsync; } catch { /* best-effort */ }
                try { _channel.Dispose(); } catch { /* dead */ }
                _channel = null;
            }
            _consumerTag = null;

            try
            {
                await OpenAndBindAsync(_hostCts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Consumer {ConsumerType} rebuild failed — connection recovery will retry on next event",
                    registration.ConsumerType.Name);
            }
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        // Capture the channel into a local so a concurrent restart doesn't
        // null it out mid-handler.
        var channel = _channel;
        if (channel is null) return;

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
                var signingKey = scope.ServiceProvider.GetService<IMessagingSigningKey>();
                try
                {
                    PipelineContextBridge.HydrateContext(pipelineContext, context, signingKey);
                }
                catch (IdentitySignatureMismatchException ex)
                {
                    // Fail loud + DLQ immediately. The queue is bound to a
                    // dead-letter exchange (see OpenAndBindAsync), so a Nack
                    // with requeue=false routes the message straight to the
                    // DLQ without spending RetryCount cycles requeuing it.
                    // Mirrors AzureServiceBusMessageBus.cs (PR #918) for the
                    // ASB leg of the same fix (issue #970).
                    IdentitySignatureMismatchCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("exchange", ea.Exchange),
                        new KeyValuePair<string, object?>("service", serviceName));
                    logger.LogError(
                        "Dead-lettering RMQ message {MessageId} from {Exchange}: identity-signature-invalid ({Reason})",
                        ea.BasicProperties.MessageId, ea.Exchange, ex.Message);
                    try
                    {
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                    catch (AlreadyClosedException)
                    {
                        // Channel died mid-nack; the broker will redeliver on
                        // reconnect and we'll re-hit this same branch then.
                    }
                    return;
                }
                catch (Exception ex)
                {
                    // Any other hydration failure (malformed baggage, config
                    // error) is a deploy bug — DLQ so it surfaces loudly
                    // instead of dispatching with a half-populated principal.
                    HydrationErrorCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("exchange", ea.Exchange),
                        new KeyValuePair<string, object?>("service", serviceName));
                    logger.LogError(
                        ex,
                        "Dead-lettering RMQ message {MessageId} from {Exchange}: pipeline-context hydration failed",
                        ea.BasicProperties.MessageId, ea.Exchange);
                    try
                    {
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                    catch (AlreadyClosedException)
                    {
                    }
                    return;
                }

                // Set ambient context so consumers can access it via PipelineContext.Current
                if (pipelineContext is PipelineContext concrete)
                    PipelineContext.SetCurrent(concrete);
            }

            var consumerInstance = scope.ServiceProvider.GetRequiredService(registration.ConsumerType);
            await registration.GetDispatcher().DispatchAsync(consumerInstance, message, context, CancellationToken.None);

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            _getOnMessageConsumed()?.Invoke(message, registration.MessageType);
        }
        catch (AlreadyClosedException)
        {
            // Channel died mid-dispatch. The shutdown handler is already
            // arranging a restart; don't try to ack/nack a dead channel.
            logger.LogWarning(
                "Consumer {ConsumerType}: channel closed during dispatch — restart in progress",
                registration.ConsumerType.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error consuming message from {Exchange} (delivery #{Count})",
                ea.Exchange, deliveryCount);

            // Requeue if under retry limit, otherwise dead-letter
            var shouldRequeue = deliveryCount < settings.RetryCount;
            try
            {
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: shouldRequeue);
            }
            catch (AlreadyClosedException)
            {
                // Same as above — restart will pick up the unacked message
                // when it comes back; broker requeues it after consumer-tag
                // cancellation.
            }

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
        _stopping = true;
        try { connection.RecoverySucceededAsync -= OnConnectionRecoveryAsync; } catch { /* best-effort */ }
        _hostCts?.Cancel();
        try
        {
            if (_channel is not null)
            {
                try { _channel.ChannelShutdownAsync -= OnChannelShutdownAsync; } catch { /* best-effort */ }
                if (_channel.IsOpen)
                {
                    if (_consumerTag != null)
                        await _channel.BasicCancelAsync(_consumerTag, cancellationToken: cancellationToken);

                    await _channel.CloseAsync(cancellationToken);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (AlreadyClosedException) { }
        finally
        {
            _channel?.Dispose();
            _channel = null;
            _hostCts?.Dispose();
            _restartLock.Dispose();
        }
    }
}
