using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Publishes messages to Azure Service Bus topics and queues.
///
/// Topic publishes are pre-flight checked against a
/// <see cref="PublisherSubjectGuard"/> loaded at bus startup. If no
/// subscription on the target topic has a correlation filter for the
/// message's Subject (and no wildcard subscription would catch it),
/// the publish throws rather than letting the broker silently discard
/// the message. Queue sends bypass the guard — queues accumulate
/// messages rather than silent-dropping them, so the failure mode is
/// observable through queue depth metrics.
/// </summary>
public sealed class AzureServiceBusPublisher(
    ServiceBusClient client,
    AzureServiceBusSettings settings,
    PublisherSubjectGuard subjectGuard,
    Func<string, string, CancellationToken, Task<int>> claimCheckRouteCounter,
    IClaimCheckStore? claimCheckStore = null) : IMessagePublisher
{
    public async Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        await PublishAsync(message, settings.TopicName, null, cancellationToken);
    }

    public async Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        await PublishAsync(message, topic, null, cancellationToken);
    }

    public async Task PublishAsync<TMessage>(TMessage message, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        await PublishAsync(message, settings.TopicName, contextHeaders, cancellationToken);
    }

    public async Task PublishAsync<TMessage>(TMessage message, string topic, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        // Fail loud before opening a sender: if no subscription will
        // catch this Subject the broker would silently discard the
        // message and the publisher would have no way to know. The
        // guard is built at bus startup from live topology and is only
        // valid for the topic it was built against; off-topic publishes
        // (rare) skip the check.
        if (string.Equals(topic, subjectGuard.TopicName, StringComparison.Ordinal))
            subjectGuard.EnsureCanPublish(typeof(TMessage).Name);

        await using var sender = client.CreateSender(topic);
        var sbMessage = await CreateMessageAsync(
            message,
            contextHeaders,
            topic,
            cancellationToken);
        await sender.SendMessageAsync(sbMessage, cancellationToken);
    }

    public async Task SendAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        await SendAsync(message, queueName, null, cancellationToken);
    }

    public async Task SendAsync<TMessage>(TMessage message, string queueName, IReadOnlyDictionary<string, string>? contextHeaders, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        await using var sender = client.CreateSender(queueName);
        var sbMessage = await CreateMessageAsync(
            message,
            contextHeaders,
            topic: null,
            cancellationToken);
        await sender.SendMessageAsync(sbMessage, cancellationToken);
    }

    private async Task<ServiceBusMessage> CreateMessageAsync<TMessage>(
        TMessage message,
        IReadOnlyDictionary<string, string>? contextHeaders,
        string? topic,
        CancellationToken cancellationToken)
        where TMessage : class
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);

        // Transport-level claim-check: if the serialized body exceeds the
        // threshold AND a store is registered, the body is offloaded and
        // replaced by a tiny placeholder carrying the reference in a reserved
        // application property. No-op (body sent inline) when no store is
        // registered or the body is small. See ClaimCheck.
        var expectedConsumers = 1;
        if (claimCheckStore is not null
            && json.Length > ClaimCheck.DefaultThresholdBytes
            && topic is not null)
        {
            expectedConsumers = await claimCheckRouteCounter(
                topic, typeof(TMessage).Name, cancellationToken);
        }

        var result = await ClaimCheck.OffloadAsync(
            json,
            "application/json",
            claimCheckStore,
            expectedConsumers: expectedConsumers,
            cancellationToken: cancellationToken);

        var sbMessage = new ServiceBusMessage(result.Body)
        {
            ContentType = "application/json",
            Subject = typeof(TMessage).Name
        };

        sbMessage.ApplicationProperties["MessageType"] = typeof(TMessage).AssemblyQualifiedName;

        if (result.Reference is Guid reference)
        {
            sbMessage.ApplicationProperties[ClaimCheck.HeaderKey] = reference.ToString("D");
        }

        if (contextHeaders != null)
        {
            foreach (var (key, value) in contextHeaders)
            {
                sbMessage.ApplicationProperties[$"ctx-{key}"] = value;
            }
        }

        return sbMessage;
    }
}
