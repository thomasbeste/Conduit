using System.Text;
using System.Text.Json;
using Conduit.Mediator;
using Conduit.Messaging.AzureServiceBus;
using Conduit.Messaging.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Messaging.Tests;

/// <summary>
/// Covers #893: the ASB consumer must hydrate <see cref="IPipelineContext"/>
/// from incoming <c>ctx-*</c> headers before dispatching, so that downstream
/// authorisation (GpiContext.UserId/TenantId/Role/GroupIds/SessionId) sees
/// the publisher's identity rather than a default empty principal.
///
/// Drives <see cref="AzureServiceBusMessageBus.ProcessMessageAsync"/>
/// directly with canned inputs + lambdas standing in for the ASB SDK's
/// complete/dead-letter/abandon callbacks (the SDK types have internal
/// constructors so we can't build a real <c>ProcessMessageEventArgs</c>
/// — same constraint that pushed <c>VerifyTopologyAsync</c> to the same
/// extract-and-test pattern).
/// </summary>
public class AzureServiceBusHydrationTests
{
    private const string TopicName = "gpi-events";
    private const string ServiceName = "service-test";
    private const string SubscriptionName = "service-test-testmessage";

    private sealed record TestMessage(string Payload);

    private sealed class TestConsumer(IPipelineContext? pipelineContext = null) : IMessageConsumer<TestMessage>
    {
        public TestMessage? Received { get; private set; }
        public MessageContext? ReceivedContext { get; private set; }
        public string? ObservedTenant { get; private set; }
        public string? ObservedUser { get; private set; }
        public string? ObservedRole { get; private set; }
        public string? ObservedSession { get; private set; }
        public string? ObservedGroups { get; private set; }
        public int DispatchCount { get; private set; }

        public Task ConsumeAsync(TestMessage message, MessageContext context, CancellationToken cancellationToken = default)
        {
            Received = message;
            ReceivedContext = context;
            DispatchCount++;
            if (pipelineContext is not null)
            {
                ObservedTenant = pipelineContext.GetBaggage("tenant_id");
                ObservedUser = pipelineContext.GetBaggage("user_id");
                ObservedRole = pipelineContext.GetBaggage("user_role");
                ObservedSession = pipelineContext.GetBaggage("session_id");
                ObservedGroups = pipelineContext.GetBaggage("group_ids");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FixedKey(byte[] key) : IMessagingSigningKey
    {
        public byte[] GetKey() => key;
    }

    private static IMessagingSigningKey KeyA() =>
        new FixedKey(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));

    private static IMessagingSigningKey KeyB() =>
        new FixedKey(Encoding.UTF8.GetBytes("ffffffffffffffffffffffffffffffff"));

    private static PipelineContext NewIdentityCtx()
    {
        var ctx = new PipelineContext();
        ctx.SetBaggage("tenant_id", "11111111-1111-1111-1111-111111111111");
        ctx.SetBaggage("user_id", "22222222-2222-2222-2222-222222222222");
        ctx.SetBaggage("user_role", "User");
        ctx.SetBaggage("group_ids", "33333333-3333-3333-3333-333333333333,44444444-4444-4444-4444-444444444444");
        ctx.SetBaggage("session_id", "55555555-5555-5555-5555-555555555555");
        return ctx;
    }

    /// <summary>
    /// Build an <c>IncomingAsbMessage</c> the way <see cref="AzureServiceBusPublisher"/>
    /// builds a real one: ctx-prefixed headers in ApplicationProperties +
    /// JSON-serialised body.
    /// </summary>
    private static AzureServiceBusMessageBus.IncomingAsbMessage BuildIncoming(
        TestMessage payload,
        IReadOnlyDictionary<string, string> contextHeaders)
    {
        var props = new Dictionary<string, object>
        {
            ["MessageType"] = typeof(TestMessage).AssemblyQualifiedName!,
        };
        foreach (var (k, v) in contextHeaders)
        {
            props[$"ctx-{k}"] = v;
        }
        return new AzureServiceBusMessageBus.IncomingAsbMessage(
            Body: JsonSerializer.Serialize(payload),
            MessageId: Guid.NewGuid().ToString(),
            DeliveryCount: 1,
            ApplicationProperties: props);
    }

    private sealed class CallbackRecorder
    {
        public bool CompleteCalled { get; private set; }
        public bool AbandonCalled { get; private set; }
        public string? DeadLetterReason { get; private set; }
        public string? DeadLetterDescription { get; private set; }

        public Func<CancellationToken, Task> Complete => _ =>
        {
            CompleteCalled = true;
            return Task.CompletedTask;
        };

        public Func<string, string, CancellationToken, Task> DeadLetter => (reason, description, _) =>
        {
            DeadLetterReason = reason;
            DeadLetterDescription = description;
            return Task.CompletedTask;
        };

        public Func<CancellationToken, Task> Abandon => _ =>
        {
            AbandonCalled = true;
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// The bus opens its own DI scope per message, so to observe what the
    /// consumer + pipeline context saw, we register both as singletons —
    /// then the inner scope and the test's outer scope both resolve to the
    /// same instances. (In production these are Scoped, but the per-message
    /// scope lifetime isn't what's under test here.)
    /// </summary>
    private static ServiceProvider BuildSp(IMessagingSigningKey? consumerKey)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPipelineContext, PipelineContext>();
        services.AddSingleton<TestConsumer>();
        if (consumerKey is not null)
        {
            services.AddSingleton(consumerKey);
        }
        return services.BuildServiceProvider();
    }

    private static AzureServiceBusMessageBus BuildBus(IServiceProvider sp) =>
        new(
            new AzureServiceBusSettings { TopicName = TopicName },
            ServiceName,
            [new() { ConsumerType = typeof(TestConsumer), MessageType = typeof(TestMessage) }],
            sp,
            NullLogger<AzureServiceBusMessageBus>.Instance);

    [Fact]
    public async Task Roundtrip_Hydrates_Identity_Into_PipelineContext_And_Completes()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA());

        var sp = BuildSp(KeyA());
        var bus = BuildBus(sp);

        var recorder = new CallbackRecorder();
        var input = BuildIncoming(new TestMessage("hello"), headers);

        await bus.ProcessMessageAsync(
            input,
            consumerType: typeof(TestConsumer),
            messageType: typeof(TestMessage),
            dispatcher: ConsumerRegistration.CreateDispatcher(typeof(TestMessage)),
            subscriptionName: SubscriptionName,
            complete: recorder.Complete,
            deadLetter: recorder.DeadLetter,
            abandon: recorder.Abandon,
            cancellationToken: TestContext.Current.CancellationToken);

        var consumer = sp.GetRequiredService<TestConsumer>();
        var pipelineCtx = sp.GetRequiredService<IPipelineContext>();

        Assert.True(recorder.CompleteCalled);
        Assert.Null(recorder.DeadLetterReason);
        Assert.False(recorder.AbandonCalled);
        Assert.Equal(1, consumer.DispatchCount);
        Assert.Equal("hello", consumer.Received?.Payload);

        // The hydrated baggage must be visible on the pipeline context the
        // bus's scope sees — which (because we registered singletons) is the
        // same instance we can inspect here.
        Assert.Equal("11111111-1111-1111-1111-111111111111", pipelineCtx.GetBaggage("tenant_id"));
        Assert.Equal("22222222-2222-2222-2222-222222222222", pipelineCtx.GetBaggage("user_id"));
        Assert.Equal("User", pipelineCtx.GetBaggage("user_role"));
        Assert.Equal("55555555-5555-5555-5555-555555555555", pipelineCtx.GetBaggage("session_id"));
        Assert.Equal(
            "33333333-3333-3333-3333-333333333333,44444444-4444-4444-4444-444444444444",
            pipelineCtx.GetBaggage("group_ids"));
    }

    private async Task<(CallbackRecorder recorder, TestConsumer consumer, IPipelineContext pipelineCtx)> RunOne(
        IMessagingSigningKey? consumerKey,
        IReadOnlyDictionary<string, string> headers,
        string payload)
    {
        var sp = BuildSp(consumerKey);
        var bus = BuildBus(sp);
        var recorder = new CallbackRecorder();
        var input = BuildIncoming(new TestMessage(payload), headers);

        await bus.ProcessMessageAsync(
            input,
            consumerType: typeof(TestConsumer),
            messageType: typeof(TestMessage),
            dispatcher: ConsumerRegistration.CreateDispatcher(typeof(TestMessage)),
            subscriptionName: SubscriptionName,
            complete: recorder.Complete,
            deadLetter: recorder.DeadLetter,
            abandon: recorder.Abandon,
            cancellationToken: TestContext.Current.CancellationToken);

        return (recorder,
                sp.GetRequiredService<TestConsumer>(),
                sp.GetRequiredService<IPipelineContext>());
    }

    [Fact]
    public async Task Tampered_Signature_DLQs_And_Does_Not_Dispatch()
    {
        var headers = new Dictionary<string, string>(PipelineContextBridge.ExtractHeaders(NewIdentityCtx(), KeyA()));

        // Adversary swaps tenant_id mid-flight after the broker accepted the message.
        headers["conduit.baggage.tenant_id"] = Guid.NewGuid().ToString();

        var (recorder, consumer, _) = await RunOne(KeyA(), headers, "tampered");

        Assert.Equal(0, consumer.DispatchCount);
        Assert.False(recorder.CompleteCalled);
        Assert.False(recorder.AbandonCalled);
        Assert.Equal("IdentitySignatureMismatch", recorder.DeadLetterReason);
        Assert.Equal("identity-signature-invalid", recorder.DeadLetterDescription);
    }

    [Fact]
    public async Task Missing_Signature_Header_DLQs_And_Does_Not_Dispatch()
    {
        var headers = new Dictionary<string, string>(PipelineContextBridge.ExtractHeaders(NewIdentityCtx(), KeyA()));

        // Strip the signature; adversary hopes consumer treats absence as
        // "legacy unsigned" and dispatches anyway.
        headers.Remove(PipelineContextBridge.SignatureHeader);

        var (recorder, consumer, _) = await RunOne(KeyA(), headers, "missing-sig");

        Assert.Equal(0, consumer.DispatchCount);
        Assert.Equal("IdentitySignatureMismatch", recorder.DeadLetterReason);
    }

    [Fact]
    public async Task Wrong_Consumer_Key_DLQs_And_Does_Not_Dispatch()
    {
        var headers = PipelineContextBridge.ExtractHeaders(NewIdentityCtx(), KeyA());
        var (recorder, consumer, _) = await RunOne(KeyB(), headers, "wrong-key");

        Assert.Equal(0, consumer.DispatchCount);
        Assert.Equal("IdentitySignatureMismatch", recorder.DeadLetterReason);
    }

    [Fact]
    public async Task No_Identity_Baggage_Hydrates_And_Dispatches()
    {
        // System-initiated message (health-check ping, scheduler tick): no
        // identity baggage at all. Should hydrate successfully and dispatch
        // — non-identity baggage is valid and rides unsigned by design.
        var source = new PipelineContext();
        source.SetBaggage("correlation_id", "corr-system-001");
        source.SetBaggage("feature_flags", "beta");
        var headers = PipelineContextBridge.ExtractHeaders(source);

        Assert.False(headers.ContainsKey(PipelineContextBridge.SignatureHeader));

        // Deliberately NO IMessagingSigningKey registered — the bridge must
        // accept unsigned messages when they carry no identity baggage.
        var (recorder, consumer, pipelineCtx) = await RunOne(consumerKey: null, headers, "system");

        Assert.True(recorder.CompleteCalled);
        Assert.Null(recorder.DeadLetterReason);
        Assert.Equal(1, consumer.DispatchCount);
        Assert.Equal("system", consumer.Received?.Payload);
        Assert.Equal("corr-system-001", pipelineCtx.GetBaggage("correlation_id"));
    }

    [Fact]
    public async Task Identity_Present_But_No_Consumer_Key_DLQs()
    {
        // Misconfigured deployment: publisher signed identity but consumer
        // forgot to register IMessagingSigningKey. The bridge throws and
        // we DLQ rather than silently dispatching with empty principal.
        var headers = PipelineContextBridge.ExtractHeaders(NewIdentityCtx(), KeyA());
        var (recorder, consumer, _) = await RunOne(consumerKey: null, headers, "no-key");

        Assert.Equal(0, consumer.DispatchCount);
        Assert.Equal("IdentitySignatureMismatch", recorder.DeadLetterReason);
    }
}
