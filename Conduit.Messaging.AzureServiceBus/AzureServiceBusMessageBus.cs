using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Mediator;
using Conduit.Messaging.Bridge;
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
    // Single Meter for the ASB transport. Counter increments once per
    // DLQ'd message so operators can alert on identity-signature
    // mismatches without grepping logs. Tag set keeps cardinality bounded
    // (subscription only — never the message ID or any baggage value).
    internal static readonly Meter Meter = new("Conduit.Messaging.AzureServiceBus");

    private static readonly Counter<long> IdentitySignatureMismatchCounter =
        Meter.CreateCounter<long>(
            "messaging.asb.identity_signature_mismatch",
            unit: "{message}",
            description: "Messages dead-lettered because their identity-baggage HMAC did not verify.");

    private static readonly Counter<long> HydrationErrorCounter =
        Meter.CreateCounter<long>(
            "messaging.asb.hydration_error",
            unit: "{message}",
            description: "Messages dead-lettered because pipeline-context hydration failed for a non-signature reason.");

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

            var capturedSubscription = subscriptionName;
            processor.ProcessMessageAsync += args => ProcessMessageAsync(
                input: new IncomingAsbMessage(
                    Body: args.Message.Body.ToString(),
                    MessageId: args.Message.MessageId,
                    DeliveryCount: args.Message.DeliveryCount,
                    ApplicationProperties: args.Message.ApplicationProperties),
                consumerType: consumerType,
                messageType: messageType,
                dispatcher: dispatcher,
                subscriptionName: capturedSubscription,
                complete: ct => args.CompleteMessageAsync(args.Message, ct),
                deadLetter: (reason, description, ct) => args.DeadLetterMessageAsync(args.Message, reason, description, ct),
                abandon: ct => args.AbandonMessageAsync(args.Message, cancellationToken: ct),
                cancellationToken: args.CancellationToken);

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

    /// <summary>
    /// Per-message processing core — extracted from the processor lambda so
    /// tests can drive every path (happy, signature-mismatch, hydration error,
    /// dispatch error) with lambdas standing in for the ASB SDK's
    /// complete/dead-letter/abandon callbacks. The production wiring in
    /// <see cref="StartAsync"/> just adapts a real <c>ProcessMessageEventArgs</c>
    /// into this signature.
    /// </summary>
    internal async Task ProcessMessageAsync(
        IncomingAsbMessage input,
        Type consumerType,
        Type messageType,
        ConsumerDispatcher dispatcher,
        string subscriptionName,
        Func<CancellationToken, Task> complete,
        Func<string, string, CancellationToken, Task> deadLetter,
        Func<CancellationToken, Task> abandon,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var consumer = scope.ServiceProvider.GetRequiredService(consumerType);

            var message = JsonSerializer.Deserialize(input.Body, messageType);
            if (message == null) return;

            var headers = new Dictionary<string, string>();
            foreach (var (key, value) in input.ApplicationProperties)
            {
                if (key.StartsWith("ctx-") && value is string val)
                {
                    headers[key[4..]] = val;
                }
            }

            var context = new MessageContext
            {
                MessageId = Guid.TryParse(input.MessageId, out var mid) ? mid : Guid.NewGuid(),
                Headers = headers,
                DeliveryCount = input.DeliveryCount,
                SourceAddress = settings.TopicName,
                DestinationAddress = subscriptionName
            };

            // Hydrate pipeline context with cross-process state (baggage,
            // causality, signed identity). Mirrors RabbitMqConsumerHost so
            // GpiContext.UserId/TenantId/Role/GroupIds/SessionId arrive
            // populated for repository ACL filters on ACA (#893).
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
                    // Fail loud + DLQ. Never dispatch with empty/forged identity.
                    // Log the WHY but never the expected/actual signature bytes.
                    IdentitySignatureMismatchCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("subscription", subscriptionName),
                        new KeyValuePair<string, object?>("topic", settings.TopicName));
                    logger.LogError(
                        "Dead-lettering ASB message {MessageId} on {Subscription}: identity-signature-invalid ({Reason})",
                        input.MessageId, subscriptionName, ex.Message);
                    await deadLetter("IdentitySignatureMismatch", "identity-signature-invalid", cancellationToken);
                    return;
                }
                catch (Exception ex)
                {
                    // Any other hydration failure (configuration error,
                    // malformed baggage, etc.) is a deploy bug — DLQ so it
                    // surfaces loudly instead of dispatching with a
                    // half-populated principal.
                    HydrationErrorCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("subscription", subscriptionName),
                        new KeyValuePair<string, object?>("topic", settings.TopicName));
                    logger.LogError(
                        ex,
                        "Dead-lettering ASB message {MessageId} on {Subscription}: pipeline-context hydration failed",
                        input.MessageId, subscriptionName);
                    await deadLetter("ContextHydrationFailed", ex.GetType().Name, cancellationToken);
                    return;
                }

                if (pipelineContext is PipelineContext concrete)
                    PipelineContext.SetCurrent(concrete);
            }

            await dispatcher.DispatchAsync(consumer, message, context, cancellationToken);

            await complete(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing message {MessageId}", input.MessageId);
            await abandon(cancellationToken);
        }
    }

    /// <summary>
    /// Test-friendly projection of <see cref="Azure.Messaging.ServiceBus.ServiceBusReceivedMessage"/>:
    /// only the fields <see cref="ProcessMessageAsync"/> actually reads. The
    /// SDK type has an internal constructor so cannot be instantiated outside
    /// the SDK — this record lets tests build canned inputs without faking
    /// the whole SDK.
    /// </summary>
    internal sealed record IncomingAsbMessage(
        string Body,
        string MessageId,
        int DeliveryCount,
        IReadOnlyDictionary<string, object> ApplicationProperties);

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
