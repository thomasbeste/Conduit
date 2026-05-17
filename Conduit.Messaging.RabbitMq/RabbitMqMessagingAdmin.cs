using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// <see cref="IMessagingAdmin"/> for RabbitMQ. Active-queue purge uses the
/// channel's <c>QueuePurge</c> primitive (no delete-and-recreate needed —
/// unlike ASB the broker has direct purge support). DLQ operations live
/// on the dead-letter exchange topology Conduit configures alongside each
/// consumer queue.
///
/// The transport for the .NET-side DLQ shape is the per-queue DLX pattern
/// Conduit emits — consumer queue X with <c>x-dead-letter-exchange</c> set
/// to <c>X.dlx</c>, which routes to a co-named DLQ <c>X.dlq</c>. We open a
/// direct channel against those names rather than chasing the topology
/// dynamically.
/// </summary>
public class RabbitMqMessagingAdmin(
    RabbitMqSettings settings,
    ILogger<RabbitMqMessagingAdmin> logger) : IMessagingAdmin
{
    private async Task<IConnection> ConnectAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            UserName = settings.Username,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
        };
        return await factory.CreateConnectionAsync(ct);
    }

    public async Task<DrainResult> PurgeActiveAsync(string name, CancellationToken ct = default)
    {
        await using var connection = await ConnectAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        var purgedCount = await channel.QueuePurgeAsync(name, ct);
        logger.LogWarning("Purged {Count} active messages from RabbitMQ queue {Name}", purgedCount, name);
        return new DrainResult(purgedCount, 0);
    }

    public async Task<DrainResult> PurgeDeadLetterAsync(string name, CancellationToken ct = default)
    {
        var dlqName = $"{name}.dlq";
        await using var connection = await ConnectAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        var purgedCount = await channel.QueuePurgeAsync(dlqName, ct);
        logger.LogWarning("Purged {Count} DLQ messages from RabbitMQ queue {Name}", purgedCount, dlqName);
        return new DrainResult(purgedCount, 0);
    }

    public async Task<DrainResult> RedeliverDeadLetterAsync(string name, int max, CancellationToken ct = default)
    {
        if (max <= 0) return new DrainResult(0, 0);
        var dlqName = $"{name}.dlq";

        await using var connection = await ConnectAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        long redelivered = 0;
        while (!ct.IsCancellationRequested && redelivered < max)
        {
            // BasicGet pulls one message at a time. Slower than a consumer
            // but correctness is easier — receive-republish-ack atomically
            // per message rather than batch-then-fail-half-way.
            var result = await channel.BasicGetAsync(dlqName, autoAck: false, ct);
            if (result is null) break;

            // Re-publish to the original queue via the DLX-paired exchange.
            // The body + properties (correlation id, headers, content type)
            // ride through unchanged so the consumer treats it like the
            // first delivery.
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: name,
                mandatory: false,
                basicProperties: new BasicProperties(result.BasicProperties),
                body: result.Body,
                cancellationToken: ct);

            await channel.BasicAckAsync(result.DeliveryTag, multiple: false, ct);
            redelivered++;
        }

        // MessageCountAsync is a passive declare — returns current depth
        // without altering the queue. Lets the UI tell the operator
        // "redelivered 100, still 47 in DLQ" so they can decide whether
        // to fire another redeliver round.
        var remaining = await SafeMessageCountAsync(channel, dlqName);

        logger.LogInformation(
            "Redelivered {Count} DLQ messages to RabbitMQ queue {Name} ({Remaining} remaining)",
            redelivered, name, remaining);
        return new DrainResult(redelivered, remaining);
    }

    private static async Task<long> SafeMessageCountAsync(IChannel channel, string queueName)
    {
        try
        {
            return await channel.MessageCountAsync(queueName);
        }
        catch
        {
            return 0;
        }
    }
}
