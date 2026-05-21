using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

                // Topic + subscriptions must be pre-created by infrastructure
                // (topic via Bicep/Helm, subscriptions via the post-update
                // runner that consumes per-service manifests). Verifying here
                // turns "topology missing" into a loud boot failure rather
                // than a silent publish drop minutes later.
                await VerifyTopologyAsync(
                    (name, ct) => TopicExistsAsync(_adminClient!, name, ct),
                    (topic, sub, ct) => SubscriptionExistsAsync(_adminClient!, topic, sub, ct),
                    settings.TopicName, serviceName, consumerRegistrations, cancellationToken);

                // Build the publish-side guard from the live topology. The
                // guard refuses to publish a Subject that has no matching
                // subscription correlation filter — without it, the broker
                // silently discards mis-routed messages. See PublisherSubjectGuard.
                var publisherGuard = await DiscoverPublisherGuardAsync(
                    _adminClient!, settings.TopicName, cancellationToken);
                logger.LogInformation(
                    "Publish-side topology discovered for {Topic}: {SubjectCount} subject(s) routable, hasWildcard={HasWildcard}",
                    settings.TopicName, publisherGuard.KnownSubjects.Count, publisherGuard.HasWildcardSubscription);

                _publisher = new AzureServiceBusPublisher(_client, settings, publisherGuard);
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

        // VerifyTopologyAsync (called above) already threw if any subscription
        // is missing — so by this point every subscriptionName is guaranteed
        // to exist on the broker. We just need to start a processor per one.
        foreach (var reg in consumerRegistrations)
        {
            var subscriptionName = BuildSubscriptionName(serviceName, reg.MessageType.Name);

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

    // Azure Service Bus enforces a 50-character limit on subscription names.
    // The natural "{service}-{messageType}" form crosses 50 with longer message
    // type names (e.g. "service-indexing-worker-sharepointchangenotification"
    // is 52). The SDK rejects creation client-side with ArgumentException, the
    // bus's StartAsync throws, _started never flips true, and the messaging
    // health check then forever reports Disconnected.
    //
    // Long names get a deterministic short hash suffix: same message type →
    // same subscription across pods/restarts, no collisions. Short names pass
    // through unchanged so existing subscriptions aren't orphaned.
    public static string BuildSubscriptionName(string serviceName, string messageTypeName)
    {
        const int MaxLength = 50;
        var raw = $"{serviceName}-{messageTypeName}".ToLowerInvariant();
        if (raw.Length <= MaxLength) return raw;

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var shortHash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        var prefixLen = MaxLength - 1 - shortHash.Length; // 50 - 1 - 8 = 41
        return $"{raw[..prefixLen]}-{shortHash}";
    }

    /// <summary>
    /// Verifies the broker has every (topic, subscription) the bus is about to
    /// bind processors to. Pure logic — extracted so tests can exercise the
    /// failure paths without needing a real ASB or a mock framework. Production
    /// passes adapters around <see cref="ServiceBusAdministrationClient"/>;
    /// tests pass lambdas with predetermined boolean answers.
    /// </summary>
    internal static async Task VerifyTopologyAsync(
        Func<string, CancellationToken, Task<bool>> topicExists,
        Func<string, string, CancellationToken, Task<bool>> subscriptionExists,
        string topicName,
        string serviceName,
        IEnumerable<ConsumerRegistration> consumerRegistrations,
        CancellationToken ct)
    {
        if (!await topicExists(topicName, ct))
        {
            throw new InvalidOperationException(
                $"Service Bus topic '{topicName}' does not exist. " +
                "Topics are pre-created by infrastructure — check Bicep / Helm.");
        }

        // Subscriptions, their MaxDeliveryCount / TTL / LockDuration /
        // correlation filter are pre-created by the post-update runner from
        // the per-service manifest baked into its image (publish.sh →
        // publish/manifests/<service>.json → /app/manifests/). Lazy auto-create
        // was removed: pods no longer declare topology, so a missing
        // subscription is a deploy bug and surfaces as a loud boot failure
        // rather than the silent-zombie drop pattern from the 2026-05-15
        // incident (pod scaled to 0 → subscription disappeared → published
        // msgs discarded with no consumer).
        foreach (var reg in consumerRegistrations)
        {
            var subscriptionName = BuildSubscriptionName(serviceName, reg.MessageType.Name);
            if (!await subscriptionExists(topicName, subscriptionName, ct))
            {
                throw new InvalidOperationException(
                    $"Service Bus subscription '{subscriptionName}' does not exist on topic '{topicName}'. " +
                    "Subscriptions are pre-created by the post-update runner from each service's manifest. " +
                    "Either the manifest is missing this consumer or the runner didn't run for this release.");
            }
        }
    }

    private static async Task<bool> TopicExistsAsync(ServiceBusAdministrationClient admin, string name, CancellationToken ct)
        => (await admin.TopicExistsAsync(name, ct)).Value;

    private static async Task<bool> SubscriptionExistsAsync(ServiceBusAdministrationClient admin, string topic, string sub, CancellationToken ct)
        => (await admin.SubscriptionExistsAsync(topic, sub, ct)).Value;

    /// <summary>
    /// Pure-logic helper: given a list of (subscription, rule) summaries,
    /// produces the <see cref="PublisherSubjectGuard"/> the publisher uses
    /// to refuse silent-drop publishes. A wildcard / SQL filter on any
    /// subscription makes the guard permissive — we only block when we
    /// can prove no subscriber would catch the subject. Extracted so
    /// tests drive it with canned topology, no real admin client needed.
    /// </summary>
    internal static PublisherSubjectGuard BuildPublisherGuard(
        string topicName,
        IEnumerable<SubscriptionRuleSummary> rules)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var hasWildcard = false;

        foreach (var r in rules)
        {
            switch (r.Kind)
            {
                case RuleFilterKind.Correlation when !string.IsNullOrEmpty(r.CorrelationSubject):
                    subjects.Add(r.CorrelationSubject);
                    break;
                case RuleFilterKind.True:
                case RuleFilterKind.Sql:
                    // SQL filters can route by arbitrary expressions
                    // (e.g. user.role = 'admin') — we cannot statically
                    // decide whether a given Subject will or won't match,
                    // so we have to assume it might.
                    hasWildcard = true;
                    break;
            }
        }

        return new PublisherSubjectGuard(subjects, hasWildcard, topicName);
    }

    /// <summary>
    /// Production adapter: enumerates every subscription on the topic +
    /// every rule on each subscription, classifies each rule, and hands
    /// the list to <see cref="BuildPublisherGuard"/>. Called once at bus
    /// startup.
    /// </summary>
    private static async Task<PublisherSubjectGuard> DiscoverPublisherGuardAsync(
        ServiceBusAdministrationClient admin,
        string topicName,
        CancellationToken ct)
    {
        var rules = new List<SubscriptionRuleSummary>();
        await foreach (var sub in admin.GetSubscriptionsAsync(topicName, ct))
        {
            await foreach (var rule in admin.GetRulesAsync(topicName, sub.SubscriptionName, ct))
            {
                rules.Add(rule.Filter switch
                {
                    CorrelationRuleFilter c => new SubscriptionRuleSummary(
                        sub.SubscriptionName, RuleFilterKind.Correlation, c.Subject),
                    TrueRuleFilter => new SubscriptionRuleSummary(
                        sub.SubscriptionName, RuleFilterKind.True, null),
                    FalseRuleFilter => new SubscriptionRuleSummary(
                        sub.SubscriptionName, RuleFilterKind.False, null),
                    SqlRuleFilter => new SubscriptionRuleSummary(
                        sub.SubscriptionName, RuleFilterKind.Sql, null),
                    _ => new SubscriptionRuleSummary(
                        sub.SubscriptionName, RuleFilterKind.Sql, null),  // unknown → treat as opaque
                });
            }
        }
        return BuildPublisherGuard(topicName, rules);
    }
}
