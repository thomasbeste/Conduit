using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Publishes messages to Azure Service Bus topics and queues.
/// </summary>
public sealed class AzureServiceBusPublisher(
    ServiceBusClient client,
    AzureServiceBusSettings settings,
    ILogger logger) : IMessagePublisher
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
        await using var sender = client.CreateSender(topic);
        var sbMessage = CreateMessage(message, contextHeaders);
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
        var sbMessage = CreateMessage(message, contextHeaders);
        await sender.SendMessageAsync(sbMessage, cancellationToken);
    }

    private static ServiceBusMessage CreateMessage<TMessage>(TMessage message, IReadOnlyDictionary<string, string>? contextHeaders)
        where TMessage : class
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = typeof(TMessage).Name
        };

        sbMessage.ApplicationProperties["MessageType"] = typeof(TMessage).AssemblyQualifiedName;

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
