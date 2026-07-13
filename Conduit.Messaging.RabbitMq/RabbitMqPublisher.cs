using System.Collections.Concurrent;
using Conduit.Messaging.Serialization;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of IMessagePublisher.
/// Publishes to type-based fanout exchanges.
///
/// Channel lifecycle: the publisher acquires its channel lazily from the
/// long-lived <see cref="IConnection"/> the bus owns. When the broker
/// initiates a connection close (e.g. RabbitMQ pod restart → code 320
/// CONNECTION_FORCED), the cached channel becomes invalid and any
/// subsequent <c>BasicPublishAsync</c> throws <see cref="AlreadyClosedException"/>.
/// The connection's AutomaticRecovery (configured on the factory in
/// <see cref="RabbitMqMessageBus"/>) brings the connection back, but
/// the channel reference doesn't follow — we re-acquire on the next
/// publish.
///
/// Without this, every publisher in the process holds a dead channel
/// forever after a broker restart and silently drops every message
/// (incident 2026-05-16: PII access + RehydrationMessage + audit events
/// all stopped landing).
/// </summary>
public sealed class RabbitMqPublisher(
    Func<CancellationToken, Task<IConnection>> connectionProvider,
    Func<string, string, string, CancellationToken, Task<int>> routeCounter,
    ILogger logger,
    IClaimCheckStore? claimCheckStore = null) : IMessagePublisher, IAsyncDisposable
{
    /// <summary>
    /// Tracks exchanges declared on the CURRENT channel. Reset whenever the
    /// channel is recreated — RabbitMQ remembers declarations broker-side
    /// via AutomaticRecovery's topology recovery, but the local cache keeps
    /// the per-publish work to a single dictionary lookup.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _declaredExchanges = new();

    /// <summary>
    /// Cached channel. Created lazily on first publish, dropped + recreated
    /// when a publish throws <see cref="AlreadyClosedException"/>. The
    /// SemaphoreSlim serialises creation so a burst of concurrent publishes
    /// after a broker hiccup doesn't open N channels.
    /// </summary>
    private IChannel? _channel;
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
        => PublishAsync(message, (IReadOnlyDictionary<string, string>?)null, cancellationToken);

    public async Task PublishAsync<TMessage>(TMessage message, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var exchangeName = MessageSerializer.GetExchangeName(typeof(TMessage));
        var (body, properties) = await BuildPayloadAsync(
            message, contextHeaders, exchangeName, "fanout", routingKey: "", cancellationToken);
        await PublishWithRecoveryAsync(exchangeName, "fanout", routingKey: "", properties, body, cancellationToken);

        logger.LogDebug("Published {MessageType} to exchange {Exchange}", typeof(TMessage).Name, exchangeName);
    }

    public Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
        where TMessage : class
        => PublishAsync(message, topic, null, cancellationToken);

    public async Task PublishAsync<TMessage>(TMessage message, string topic, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var exchangeName = MessageSerializer.GetExchangeName(typeof(TMessage));
        var (body, properties) = await BuildPayloadAsync(
            message, contextHeaders, exchangeName, "topic", topic, cancellationToken);
        await PublishWithRecoveryAsync(exchangeName, "topic", routingKey: topic, properties, body, cancellationToken);

        logger.LogDebug("Published {MessageType} to exchange {Exchange} with topic {Topic}",
            typeof(TMessage).Name, exchangeName, topic);
    }

    public Task SendAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        where TMessage : class
        => SendAsync(message, queueName, null, cancellationToken);

    public async Task SendAsync<TMessage>(TMessage message, string queueName, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var (body, properties) = await BuildPayloadAsync(
            message, contextHeaders, exchangeName: "", exchangeType: null, queueName, cancellationToken);

        // Direct-to-queue via the default exchange. No exchange declare needed
        // (the default "" exchange always exists); still funnel through the
        // recovery wrapper so a broker restart doesn't blackhole sends either.
        await PublishWithRecoveryAsync(exchangeName: "", exchangeType: null, routingKey: queueName,
            properties, body, cancellationToken);

        logger.LogDebug("Sent {MessageType} to queue {Queue}", typeof(TMessage).Name, queueName);
    }

    private async Task<(ReadOnlyMemory<byte> Body, BasicProperties Properties)> BuildPayloadAsync<TMessage>(
        TMessage message,
        IReadOnlyDictionary<string, string>? contextHeaders,
        string exchangeName,
        string? exchangeType,
        string routingKey,
        CancellationToken cancellationToken) where TMessage : class
    {
        var headers = contextHeaders is not null ? new Dictionary<string, string>(contextHeaders) : null;
        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name, headers);

        // Transport-level claim-check: offload a >threshold serialized envelope
        // to the store and replace it with a tiny placeholder body, carrying the
        // reference in a reserved AMQP header. ctx headers ride INSIDE the
        // serialized envelope (MessageSerializer), but the claim-check ref must
        // ride on the AMQP BasicProperties.Headers — the body itself is now just
        // the placeholder, so an in-body header would be offloaded away with it.
        // No-op (body sent inline, no header) when no store is registered or the
        // body is small. See ClaimCheck.
        var expectedConsumers = 1;
        if (claimCheckStore is not null
            && body.Length > ClaimCheck.DefaultThresholdBytes
            && exchangeType is not null)
        {
            expectedConsumers = await routeCounter(
                exchangeName, exchangeType, routingKey, cancellationToken);
        }

        var offloaded = await ClaimCheck.OffloadAsync(
            body,
            "application/json",
            claimCheckStore,
            expectedConsumers: expectedConsumers,
            cancellationToken: cancellationToken);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = contextHeaders?.GetValueOrDefault("conduit.correlation-id"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        if (offloaded.Reference is Guid reference)
        {
            properties.Headers = new Dictionary<string, object?>
            {
                [ClaimCheck.HeaderKey] = reference.ToString("D")
            };
        }

        return (offloaded.Body, properties);
    }

    /// <summary>
    /// Single-retry publish: try once with the cached channel. If the channel
    /// is closed (<see cref="AlreadyClosedException"/>), drop it, re-acquire
    /// from the connection, and retry once. Anything past the retry —
    /// including a connection that hasn't yet been auto-recovered —
    /// propagates so the caller sees the failure.
    /// </summary>
    private async Task PublishWithRecoveryAsync(
        string exchangeName, string? exchangeType, string routingKey,
        BasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var channel = await GetOrCreateChannelAsync(cancellationToken);
                if (exchangeType is not null)
                    await EnsureExchangeDeclaredAsync(channel, exchangeName, exchangeType, cancellationToken);
                // mandatory: true — an UNROUTABLE message (no bound queue at publish time:
                // a binding/reconnect race under burst, a not-yet-reconciled subscription)
                // is otherwise silently ACKED by the broker and dropped, even with publisher
                // confirms on. With mandatory + confirmation tracking the broker returns it and
                // the publish faults, so the caller re-drives instead of leaving a work-unit
                // marked "dispatched" for a command that never reached its queue. Every
                // published type has a pre-created durable subscription, so a persistent
                // unroutable is a real bug that must fail loud, not vanish.
                await channel.BasicPublishAsync(exchangeName, routingKey, mandatory: true, properties, body, cancellationToken);
                return;
            }
            catch (AlreadyClosedException ex) when (attempt == 1)
            {
                logger.LogWarning(
                    "RabbitMQ channel closed ({Reason}) — recreating and retrying once",
                    ex.ShutdownReason?.ReplyText ?? "unknown");
                await ResetChannelAsync();
            }
        }
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        var existing = _channel;
        if (existing is { IsOpen: true })
            return existing;

        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            if (_channel is not null)
            {
                try { _channel.Dispose(); } catch { /* best-effort cleanup of the dead channel */ }
            }
            // Resolve a live connection — the provider recreates the underlying
            // IConnection if the previous one died permanently (not just the
            // channel), so a publisher can't be stranded on a dead connection.
            var connection = await connectionProvider(cancellationToken);
            // Publisher confirms are MANDATORY, not optional. Without them BasicPublishAsync
            // is fire-and-forget: it returns the instant the bytes hit the socket buffer and
            // the caller believes the message was delivered even when the broker silently
            // dropped it (burst backpressure, a channel/connection blip). That silent loss is
            // how a work-unit gets marked "dispatched" for a command that never reached the
            // queue — a permanent zombie whose completion never comes. With confirmations +
            // tracking enabled, BasicPublishAsync awaits the broker ack and THROWS on nack /
            // unroutable / timeout, so a lost publish fails loud, the dispatching message
            // nacks + redelivers, and delivery is actually reliable.
            _channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken: cancellationToken);
            // Surface unroutable returns loudly. With confirmation tracking + mandatory:true
            // an unroutable publish also faults the BasicPublishAsync task (so the caller
            // re-drives); this handler makes the CAUSE visible instead of a bare exception —
            // exchange + routing key + reply text pinpoint the missing binding/subscription.
            _channel.BasicReturnAsync += (_, ea) =>
            {
                logger.LogError(
                    "RabbitMQ returned an UNROUTABLE message: exchange='{Exchange}' routingKey='{RoutingKey}' reply={ReplyCode}:{ReplyText}. "
                    + "The publish faults and re-drives — but a persistent return means a missing binding/subscription.",
                    ea.Exchange, ea.RoutingKey, ea.ReplyCode, ea.ReplyText);
                return Task.CompletedTask;
            };
            _declaredExchanges.Clear();
            logger.LogInformation("RabbitMQ publish channel created (publisher confirms ON, mandatory ON)");
            return _channel;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task ResetChannelAsync()
    {
        await _channelLock.WaitAsync();
        try
        {
            if (_channel is not null)
            {
                try { _channel.Dispose(); } catch { /* the channel is already in a bad state */ }
                _channel = null;
                _declaredExchanges.Clear();
            }
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task EnsureExchangeDeclaredAsync(IChannel channel, string exchangeName, string type, CancellationToken cancellationToken)
    {
        if (_declaredExchanges.TryAdd(exchangeName, true))
        {
            await channel.ExchangeDeclareAsync(exchangeName, type, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channelLock.WaitAsync();
        try
        {
            if (_channel is not null)
            {
                try { await _channel.CloseAsync(); } catch (AlreadyClosedException) { /* fine */ }
                _channel.Dispose();
                _channel = null;
            }
        }
        finally
        {
            _channelLock.Release();
            _channelLock.Dispose();
        }
    }
}
