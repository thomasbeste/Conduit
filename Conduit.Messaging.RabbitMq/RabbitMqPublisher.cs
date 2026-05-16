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
    IConnection connection,
    ILogger logger) : IMessagePublisher, IAsyncDisposable
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
        var (body, properties) = BuildPayload(message, contextHeaders);
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
        var (body, properties) = BuildPayload(message, contextHeaders);
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
        var (body, properties) = BuildPayload(message, contextHeaders);

        // Direct-to-queue via the default exchange. No exchange declare needed
        // (the default "" exchange always exists); still funnel through the
        // recovery wrapper so a broker restart doesn't blackhole sends either.
        await PublishWithRecoveryAsync(exchangeName: "", exchangeType: null, routingKey: queueName,
            properties, body, cancellationToken);

        logger.LogDebug("Sent {MessageType} to queue {Queue}", typeof(TMessage).Name, queueName);
    }

    private static (ReadOnlyMemory<byte> Body, BasicProperties Properties) BuildPayload<TMessage>(
        TMessage message, IReadOnlyDictionary<string, string>? contextHeaders) where TMessage : class
    {
        var headers = contextHeaders is not null ? new Dictionary<string, string>(contextHeaders) : null;
        var body = MessageSerializer.Serialize(message, typeof(TMessage).FullName ?? typeof(TMessage).Name, headers);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = contextHeaders?.GetValueOrDefault("conduit.correlation-id"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };
        return (body, properties);
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
                await channel.BasicPublishAsync(exchangeName, routingKey, mandatory: false, properties, body, cancellationToken);
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

            // Connection may itself be mid-recovery after a broker bounce.
            // CreateChannelAsync will throw if so — let it propagate; the
            // outer caller will retry on the next publish attempt.
            if (_channel is not null)
            {
                try { _channel.Dispose(); } catch { /* best-effort cleanup of the dead channel */ }
            }
            _channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            _declaredExchanges.Clear();
            logger.LogInformation("RabbitMQ publish channel created");
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
