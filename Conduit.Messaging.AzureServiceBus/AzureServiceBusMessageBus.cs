using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Azure Service Bus implementation of IMessageBus.
/// Uses topics for pub/sub and queues for point-to-point.
/// Each service gets its own subscription on the shared topic.
/// </summary>
public sealed class AzureServiceBusMessageBus(
    AzureServiceBusSettings settings,
    string serviceName,
    List<ConsumerRegistration> consumerRegistrations,
    IServiceProvider serviceProvider,
    ILogger<AzureServiceBusMessageBus> logger) : IMessageBus, IAsyncDisposable
{
    private ServiceBusClient? _client;
    private ServiceBusAdministrationClient? _adminClient;
    private AzureServiceBusPublisher? _publisher;
    private readonly ConcurrentBag<ServiceBusProcessor> _processors = [];
    private bool _started;

    public IMessagePublisher Publisher => _publisher
        ?? throw new InvalidOperationException("Message bus has not been started. Call StartAsync first.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;

        logger.LogInformation(
            "Starting Azure Service Bus message bus for {ServiceName}",
            serviceName);

        // Retry connection with backoff
        const int maxRetries = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _client = new ServiceBusClient(settings.ConnectionString);
                _adminClient = new ServiceBusAdministrationClient(settings.ConnectionString);

                // Ensure topic exists
                if (!await _adminClient.TopicExistsAsync(settings.TopicName, cancellationToken))
                {
                    await _adminClient.CreateTopicAsync(settings.TopicName, cancellationToken);
                    logger.LogInformation("Created topic {TopicName}", settings.TopicName);
                }

                _publisher = new AzureServiceBusPublisher(_client, settings);
                logger.LogInformation("Azure Service Bus connection established for {ServiceName}", serviceName);
                break;
            }
            catch (Exception ex) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                var delay = Math.Min(attempt * 2, 30);
                logger.LogWarning(ex, "Azure Service Bus connection attempt {Attempt}/{Max} failed, retrying in {Delay}s...",
                    attempt, maxRetries, delay);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Set up consumers as topic subscriptions
        foreach (var reg in consumerRegistrations)
        {
            var subscriptionName = $"{serviceName}-{reg.MessageType.Name}".ToLowerInvariant();

            // Ensure subscription exists with message type filter
            if (!await _adminClient.SubscriptionExistsAsync(settings.TopicName, subscriptionName, cancellationToken))
            {
                var subOptions = new CreateSubscriptionOptions(settings.TopicName, subscriptionName)
                {
                    MaxDeliveryCount = 3,
                    DefaultMessageTimeToLive = TimeSpan.FromDays(1)
                };
                var ruleOptions = new CreateRuleOptions("MessageTypeFilter", new CorrelationRuleFilter
                {
                    Subject = reg.MessageType.Name
                });
                await _adminClient.CreateSubscriptionAsync(subOptions, ruleOptions, cancellationToken);
                logger.LogInformation("Created subscription {Subscription} on {Topic}",
                    subscriptionName, settings.TopicName);
            }

            // Start processor
            var processor = _client!.CreateProcessor(settings.TopicName, subscriptionName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = settings.MaxConcurrentCalls,
                AutoCompleteMessages = false
            });

            var consumerType = reg.ConsumerType;
            var messageType = reg.MessageType;
            var dispatcher = reg.GetDispatcher();

            processor.ProcessMessageAsync += async args =>
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var consumer = scope.ServiceProvider.GetRequiredService(consumerType);

                    var message = JsonSerializer.Deserialize(args.Message.Body.ToString(), messageType);
                    if (message == null) return;

                    // Extract context headers
                    var headers = new Dictionary<string, string>();
                    foreach (var prop in args.Message.ApplicationProperties)
                    {
                        if (prop.Key.StartsWith("ctx-") && prop.Value is string val)
                        {
                            headers[prop.Key[4..]] = val;
                        }
                    }

                    var context = new MessageContext
                    {
                        MessageId = Guid.TryParse(args.Message.MessageId, out var mid) ? mid : Guid.NewGuid(),
                        Headers = headers,
                        DeliveryCount = args.Message.DeliveryCount
                    };
                    await dispatcher.DispatchAsync(consumer, message, context, args.CancellationToken);

                    await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing message {MessageId}", args.Message.MessageId);
                    await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
                }
            };

            processor.ProcessErrorAsync += args =>
            {
                logger.LogError(args.Exception, "Azure Service Bus processor error: {Source}", args.ErrorSource);
                return Task.CompletedTask;
            };

            await processor.StartProcessingAsync(cancellationToken);
            _processors.Add(processor);
        }

        _started = true;
        logger.LogInformation(
            "Azure Service Bus message bus started for {ServiceName}: {ConsumerCount} consumers registered",
            serviceName, consumerRegistrations.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;

        logger.LogInformation("Stopping Azure Service Bus message bus for {ServiceName}", serviceName);

        foreach (var processor in _processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }

        if (_client != null)
        {
            await _client.DisposeAsync();
        }

        _started = false;
        logger.LogInformation("Azure Service Bus message bus stopped for {ServiceName}", serviceName);
    }

    public MessageBusHealth GetHealth()
    {
        var isHealthy = _client is { IsClosed: false } && _started;
        return new MessageBusHealth
        {
            IsHealthy = isHealthy,
            Status = isHealthy ? "Connected" : "Disconnected",
            Details = new MessageBusHealthDetails
            {
                Service = serviceName,
                Host = "Azure Service Bus",
                ConsumerCount = consumerRegistrations.Count,
                Started = _started
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
