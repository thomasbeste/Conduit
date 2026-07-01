using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// <see cref="IMessagingAdmin"/> for Azure Service Bus.
///
/// Purge of the active queue uses delete-and-recreate — the ASB SDK has no
/// "purge" primitive, but recreating a subscription with the same name +
/// settings is O(1) on the broker side and gives a guaranteed empty
/// post-state. The original max-delivery + lock-duration + filter rule are
/// captured before the delete and reapplied on the recreate so the
/// subscription returns to a byte-identical configuration.
///
/// DLQ operations open a receiver on the
/// <c>{subscription}/$DeadLetterQueue</c> sub-path the ASB transport
/// surfaces and drop / republish each message in a loop that runs until
/// the broker-side count is zero or an internal timeout fires.
/// </summary>
public class AzureServiceBusMessagingAdmin(
    IOptions<AzureServiceBusSettings> options,
    ILogger<AzureServiceBusMessagingAdmin> logger) : IMessagingAdmin
{
    private const int DlqDrainBatchSize = 100;
    private static readonly TimeSpan DlqReceiveWait = TimeSpan.FromSeconds(5);

    // Admin-op time budget. Big enough to drain ~thousands of DLQ messages
    // in one click; small enough that the operator gets feedback before
    // the gateway's 100 s HttpClient default kicks in. The 2026-05-16
    // recovery needed 9 clicks for ~270 messages × 3 subs because the
    // server returned "success" the moment any single ReceiveMessagesAsync
    // came back empty (broker prefetch stall). This budget + the
    // runtime-count probe below replaces that with one click per sub.
    private static readonly TimeSpan DrainOperationTimeout = TimeSpan.FromSeconds(60);

    // Pause between an empty receive batch and the next attempt while the
    // runtime count still shows messages — almost always means leftover
    // peek-locks from prior partial drains. ASB's default LockDuration is
    // 30 s; 2 s lets us probe a few times before the budget runs out
    // without hammering the management API.
    private static readonly TimeSpan PostEmptyBatchDelay = TimeSpan.FromSeconds(2);

    private ServiceBusAdministrationClient? _adminClient;
    private ServiceBusClient? _client;

    private ServiceBusAdministrationClient AdminClient
        => _adminClient ??= new ServiceBusAdministrationClient(options.Value.ConnectionString);

    private ServiceBusClient Client
        => _client ??= new ServiceBusClient(options.Value.ConnectionString);

    public async Task<DrainResult> PurgeActiveAsync(string name, CancellationToken ct = default)
    {
        var topic = options.Value.TopicName;

        // Data-plane drain ONLY — NEVER delete/recreate the subscription.
        // Topology is owned by the post-update-runner reconciler; nothing
        // else may create or delete a subscription. An admin "clear active"
        // that deleted the subscription is the silent-zombie failure mode
        // (2026-05-15): if the recreate races/throws after the delete lands,
        // the subscription is gone, the consumer's startup subscription check
        // fails, and every publish for this message type is silently dropped
        // until the next deploy reconciles it. It would also drop messages
        // that arrive in the delete→recreate window, not just the backlog the
        // operator asked to clear. This mirrors the RabbitMQ transport's
        // in-place QueuePurge and the DLQ drain below: ReceiveAndDelete on the
        // main entity, needs only the Listen claim, leaves topology untouched.
        var receiver = Client.CreateReceiver(topic, name, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
        });

        using var opCts = new CancellationTokenSource(DrainOperationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(opCts.Token, ct);
        var token = linkedCts.Token;

        long drained = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var batch = await receiver.ReceiveMessagesAsync(DlqDrainBatchSize, DlqReceiveWait, token);
                if (batch.Count > 0)
                {
                    drained += batch.Count;
                    continue;
                }

                // Empty batch could mean truly drained OR broker prefetch
                // hasn't refilled yet. Check the authoritative runtime count
                // before exiting — non-zero means keep probing until the
                // budget runs out (same logic as the DLQ drain).
                var runtime = await SafeGetRuntimeAsync(topic, name, token);
                if (runtime is null || runtime.ActiveMessageCount == 0) break;

                try
                {
                    await Task.Delay(PostEmptyBatchDelay, token);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) when (opCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Internal timeout fired — fall through, report what we drained
            // plus authoritative remaining count.
        }
        finally
        {
            await receiver.CloseAsync(CancellationToken.None);
        }

        var finalRuntime = await SafeGetRuntimeAsync(topic, name, CancellationToken.None);
        var remaining = finalRuntime?.ActiveMessageCount ?? 0;

        logger.LogWarning(
            "Purged {Count} active messages from {Topic}/{Name} ({Remaining} remaining)",
            drained, topic, name, remaining);
        return new DrainResult(drained, remaining);
    }

    public async Task<DrainResult> PurgeDeadLetterAsync(string name, CancellationToken ct = default)
    {
        var topic = options.Value.TopicName;

        // ReceiveAndDelete: messages are removed broker-side atomically on
        // receive — no peek-lock window, so a partial drain (process dies,
        // timeout fires) doesn't leave 60 s of locked-but-undrainable
        // messages blocking the next attempt. The 2026-05-16 recovery hit
        // exactly that: each click drained ~100 then the broker briefly
        // returned no messages, the previous PeekLock code declared
        // "success — 0 in batch, must be empty", and the next click had to
        // wait those locks out.
        var receiver = Client.CreateReceiver(topic, name, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
        });

        using var opCts = new CancellationTokenSource(DrainOperationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(opCts.Token, ct);
        var token = linkedCts.Token;

        long drained = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var batch = await receiver.ReceiveMessagesAsync(DlqDrainBatchSize, DlqReceiveWait, token);
                if (batch.Count > 0)
                {
                    drained += batch.Count;
                    continue;
                }

                // Empty batch could mean truly drained OR broker hasn't
                // surfaced the next batch yet (large DLQs throttle the
                // prefetch refill). Check the authoritative runtime count
                // before exiting — non-zero usually means leftover locks
                // from a prior partial drain that haven't expired yet.
                var runtime = await SafeGetRuntimeAsync(topic, name, token);
                if (runtime is null || runtime.DeadLetterMessageCount == 0) break;

                try
                {
                    await Task.Delay(PostEmptyBatchDelay, token);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) when (opCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Internal timeout fired — fall through, report what we drained
            // plus authoritative remaining count.
        }
        finally
        {
            await receiver.CloseAsync(CancellationToken.None);
        }

        var finalRuntime = await SafeGetRuntimeAsync(topic, name, CancellationToken.None);
        var remaining = finalRuntime?.DeadLetterMessageCount ?? 0;

        logger.LogWarning(
            "Purged {Count} DLQ messages from {Topic}/{Name} ({Remaining} remaining)",
            drained, topic, name, remaining);
        return new DrainResult(drained, remaining);
    }

    public async Task<DrainResult> RedeliverDeadLetterAsync(string name, int max, CancellationToken ct = default)
    {
        if (max <= 0) return new DrainResult(0, 0);
        var topic = options.Value.TopicName;

        // PeekLock here (unlike PurgeDeadLetter) because we must NOT lose
        // the message if the republish fails — leave it abandoned, broker
        // returns it on the next attempt. The same internal timeout +
        // runtime-count check handle the broker-prefetch-stall case.
        var receiver = Client.CreateReceiver(topic, name, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });
        var sender = Client.CreateSender(topic);

        using var opCts = new CancellationTokenSource(DrainOperationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(opCts.Token, ct);
        var token = linkedCts.Token;

        long redelivered = 0;
        try
        {
            while (!token.IsCancellationRequested && redelivered < max)
            {
                var batchSize = (int)Math.Min(DlqDrainBatchSize, max - redelivered);
                var batch = await receiver.ReceiveMessagesAsync(batchSize, DlqReceiveWait, token);
                if (batch.Count == 0)
                {
                    var runtime = await SafeGetRuntimeAsync(topic, name, token);
                    if (runtime is null || runtime.DeadLetterMessageCount == 0) break;
                    try
                    {
                        await Task.Delay(PostEmptyBatchDelay, token);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                foreach (var msg in batch)
                {
                    // Reconstruct a new message preserving body + application
                    // properties + subject (the message-type filter key).
                    var replay = new ServiceBusMessage(msg.Body)
                    {
                        Subject = msg.Subject,
                        ContentType = msg.ContentType,
                        CorrelationId = msg.CorrelationId,
                        MessageId = msg.MessageId,
                    };
                    foreach (var kv in msg.ApplicationProperties)
                    {
                        replay.ApplicationProperties[kv.Key] = kv.Value;
                    }
                    await sender.SendMessageAsync(replay, token);
                    await receiver.CompleteMessageAsync(msg, token);
                    redelivered++;
                    if (redelivered >= max) break;
                }
            }
        }
        catch (OperationCanceledException) when (opCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Internal timeout — return partial count + remaining.
        }
        finally
        {
            await sender.CloseAsync(CancellationToken.None);
            await receiver.CloseAsync(CancellationToken.None);
        }

        var finalRuntime = await SafeGetRuntimeAsync(topic, name, CancellationToken.None);
        var remaining = finalRuntime?.DeadLetterMessageCount ?? 0;

        logger.LogInformation(
            "Redelivered {Count} DLQ messages to {Topic}/{Name} ({Remaining} remaining)",
            redelivered, topic, name, remaining);
        return new DrainResult(redelivered, remaining);
    }

    private async Task<SubscriptionRuntimeProperties?> SafeGetRuntimeAsync(
        string topic, string name, CancellationToken ct)
    {
        try
        {
            return (await AdminClient.GetSubscriptionRuntimePropertiesAsync(topic, name, ct)).Value;
        }
        catch
        {
            return null;
        }
    }
}
