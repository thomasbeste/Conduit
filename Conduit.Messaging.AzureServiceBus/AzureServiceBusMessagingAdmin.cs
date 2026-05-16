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
/// surfaces and complete-or-republish each message in a bounded loop.
/// </summary>
public class AzureServiceBusMessagingAdmin(
    IOptions<AzureServiceBusSettings> options,
    ILogger<AzureServiceBusMessagingAdmin> logger) : IMessagingAdmin
{
    private const int DlqDrainBatchSize = 50;
    private static readonly TimeSpan DlqReceiveWait = TimeSpan.FromSeconds(2);

    private ServiceBusAdministrationClient? _adminClient;
    private ServiceBusClient? _client;

    private ServiceBusAdministrationClient AdminClient
        => _adminClient ??= new ServiceBusAdministrationClient(options.Value.ConnectionString);

    private ServiceBusClient Client
        => _client ??= new ServiceBusClient(options.Value.ConnectionString);

    public async Task<long> PurgeActiveAsync(string name, CancellationToken ct = default)
    {
        var topic = options.Value.TopicName;

        // Capture original config before destroying. Same retry-pre-deploy
        // path that bicep would use; preserves message-type filter rule.
        var props = (await AdminClient.GetSubscriptionAsync(topic, name, ct)).Value;
        var runtime = (await AdminClient.GetSubscriptionRuntimePropertiesAsync(topic, name, ct)).Value;
        var droppedCount = runtime.ActiveMessageCount;

        // Capture rules so we don't lose the message-type filter on recreate.
        var rules = new List<RuleProperties>();
        await foreach (var rule in AdminClient.GetRulesAsync(topic, name, ct))
        {
            rules.Add(rule);
        }

        logger.LogWarning(
            "Purging ASB subscription {Topic}/{Name} — dropping {Count} active messages",
            topic, name, droppedCount);

        await AdminClient.DeleteSubscriptionAsync(topic, name, ct);

        var createOptions = new CreateSubscriptionOptions(topic, name)
        {
            MaxDeliveryCount = props.MaxDeliveryCount,
            LockDuration = props.LockDuration,
            DefaultMessageTimeToLive = props.DefaultMessageTimeToLive,
            DeadLetteringOnMessageExpiration = props.DeadLetteringOnMessageExpiration,
            EnableDeadLetteringOnFilterEvaluationExceptions = props.EnableDeadLetteringOnFilterEvaluationExceptions,
            AutoDeleteOnIdle = props.AutoDeleteOnIdle,
            EnableBatchedOperations = props.EnableBatchedOperations,
            RequiresSession = props.RequiresSession,
            ForwardTo = props.ForwardTo,
            ForwardDeadLetteredMessagesTo = props.ForwardDeadLetteredMessagesTo,
            UserMetadata = props.UserMetadata,
            Status = props.Status,
        };

        // Re-apply rules. The default $Default rule auto-creates on
        // CreateSubscriptionAsync — we delete it then add the originals
        // so we end up with exactly what was there before.
        await AdminClient.CreateSubscriptionAsync(createOptions, ct);
        try
        {
            await AdminClient.DeleteRuleAsync(topic, name, "$Default", ct);
        }
        catch
        {
            // No $Default rule — fine, originals had a custom one.
        }
        foreach (var rule in rules)
        {
            var ruleOptions = new CreateRuleOptions(rule.Name, rule.Filter) { Action = rule.Action };
            await AdminClient.CreateRuleAsync(topic, name, ruleOptions, ct);
        }

        return droppedCount;
    }

    public async Task<long> PurgeDeadLetterAsync(string name, CancellationToken ct = default)
    {
        var topic = options.Value.TopicName;
        var receiver = Client.CreateReceiver(topic, name, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });

        long completed = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await receiver.ReceiveMessagesAsync(DlqDrainBatchSize, DlqReceiveWait, ct);
                if (batch.Count == 0) break;
                foreach (var msg in batch)
                {
                    await receiver.CompleteMessageAsync(msg, ct);
                    completed++;
                }
            }
        }
        finally
        {
            await receiver.CloseAsync(CancellationToken.None);
        }

        logger.LogWarning(
            "Purged {Count} DLQ messages from {Topic}/{Name}",
            completed, topic, name);
        return completed;
    }

    public async Task<long> RedeliverDeadLetterAsync(string name, int max, CancellationToken ct = default)
    {
        if (max <= 0) return 0;
        var topic = options.Value.TopicName;

        var receiver = Client.CreateReceiver(topic, name, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });
        var sender = Client.CreateSender(topic);

        long redelivered = 0;
        try
        {
            while (!ct.IsCancellationRequested && redelivered < max)
            {
                var batchSize = (int)Math.Min(DlqDrainBatchSize, max - redelivered);
                var batch = await receiver.ReceiveMessagesAsync(batchSize, DlqReceiveWait, ct);
                if (batch.Count == 0) break;

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
                    await sender.SendMessageAsync(replay, ct);
                    await receiver.CompleteMessageAsync(msg, ct);
                    redelivered++;
                    if (redelivered >= max) break;
                }
            }
        }
        finally
        {
            await sender.CloseAsync(CancellationToken.None);
            await receiver.CloseAsync(CancellationToken.None);
        }

        logger.LogInformation(
            "Redelivered {Count} DLQ messages to {Topic}/{Name}",
            redelivered, topic, name);
        return redelivered;
    }
}
