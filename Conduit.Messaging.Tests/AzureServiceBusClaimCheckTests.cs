using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Conduit.Mediator;
using Conduit.Messaging.AzureServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Messaging.Tests;

/// <summary>
/// Covers #2432 on the ASB consume seam: <see cref="AzureServiceBusMessageBus.ProcessMessageAsync"/>
/// must rehydrate a claim-checked body from the store before deserializing, leave
/// a small inline body (no ref header) untouched, and dead-letter — not abandon —
/// when the referenced blob is gone.
///
/// Drives ProcessMessageAsync directly with canned inputs + lambdas standing in
/// for the ASB SDK callbacks, the same extract-and-test pattern the hydration
/// tests use (the SDK's message/args types have internal constructors).
/// </summary>
public class AzureServiceBusClaimCheckTests
{
    private const string TopicName = "gpi-events";
    private const string ServiceName = "service-test";
    private const string SubscriptionName = "service-test-testmessage";

    private sealed record TestMessage(string Payload);

    private sealed class TestConsumer : IMessageConsumer<TestMessage>
    {
        public TestMessage? Received { get; private set; }
        public int DispatchCount { get; private set; }

        public Task ConsumeAsync(TestMessage message, MessageContext context, CancellationToken cancellationToken = default)
        {
            Received = message;
            DispatchCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore : IClaimCheckStore
    {
        private readonly ConcurrentDictionary<Guid, byte[]> _blobs = new();
        public int GetCount { get; private set; }

        public Task<Guid> StoreAsync(ReadOnlyMemory<byte> payload, string contentType, CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            _blobs[id] = payload.ToArray();
            return Task.FromResult(id);
        }

        public Task<byte[]?> GetAsync(Guid payloadId, CancellationToken cancellationToken = default)
        {
            GetCount++;
            _blobs.TryGetValue(payloadId, out var blob);
            return Task.FromResult(blob);
        }
    }

    private sealed class CallbackRecorder
    {
        public bool CompleteCalled { get; private set; }
        public bool AbandonCalled { get; private set; }
        public string? DeadLetterReason { get; private set; }

        public Func<CancellationToken, Task> Complete => _ => { CompleteCalled = true; return Task.CompletedTask; };
        public Func<string, string, CancellationToken, Task> DeadLetter => (reason, _, _) => { DeadLetterReason = reason; return Task.CompletedTask; };
        public Func<CancellationToken, Task> Abandon => _ => { AbandonCalled = true; return Task.CompletedTask; };
    }

    private static ServiceProvider BuildSp(IClaimCheckStore? store)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TestConsumer>();
        if (store is not null) services.AddSingleton(store);
        return services.BuildServiceProvider();
    }

    private static AzureServiceBusMessageBus BuildBus(IServiceProvider sp) =>
        new(
            new AzureServiceBusSettings { TopicName = TopicName },
            ServiceName,
            [new() { ConsumerType = typeof(TestConsumer), MessageType = typeof(TestMessage) }],
            sp,
            NullLogger<AzureServiceBusMessageBus>.Instance);

    private static async Task<(CallbackRecorder recorder, TestConsumer consumer)> Run(
        IServiceProvider sp, AzureServiceBusMessageBus.IncomingAsbMessage input)
    {
        var bus = BuildBus(sp);
        var recorder = new CallbackRecorder();
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
        return (recorder, sp.GetRequiredService<TestConsumer>());
    }

    [Fact]
    public async Task Claim_Checked_Body_Is_Rehydrated_Then_Dispatched()
    {
        var store = new FakeStore();
        var sp = BuildSp(store);

        // A >threshold payload: offload it the way the publisher does, keep the
        // placeholder as the wire body + the reference in the ref header.
        var big = new TestMessage(new string('x', ClaimCheck.DefaultThresholdBytes + 1));
        var json = JsonSerializer.SerializeToUtf8Bytes(big);
        var offload = await ClaimCheck.OffloadAsync(json, "application/json", store, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(offload.Reference);

        var input = new AzureServiceBusMessageBus.IncomingAsbMessage(
            Body: Encoding.UTF8.GetString(offload.Body.Span),
            MessageId: Guid.NewGuid().ToString(),
            DeliveryCount: 1,
            ApplicationProperties: new Dictionary<string, object>
            {
                ["MessageType"] = typeof(TestMessage).AssemblyQualifiedName!,
                [ClaimCheck.HeaderKey] = offload.Reference!.Value.ToString("D"),
            });

        var (recorder, consumer) = await Run(sp, input);

        Assert.True(recorder.CompleteCalled);
        Assert.Null(recorder.DeadLetterReason);
        Assert.False(recorder.AbandonCalled);
        Assert.Equal(1, consumer.DispatchCount);
        Assert.Equal(big.Payload, consumer.Received?.Payload);
        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public async Task Small_Inline_Body_Bypasses_Store()
    {
        var store = new FakeStore();
        var sp = BuildSp(store);

        // No ref header — the body rides inline. The store must never be touched.
        var input = new AzureServiceBusMessageBus.IncomingAsbMessage(
            Body: JsonSerializer.Serialize(new TestMessage("inline")),
            MessageId: Guid.NewGuid().ToString(),
            DeliveryCount: 1,
            ApplicationProperties: new Dictionary<string, object>
            {
                ["MessageType"] = typeof(TestMessage).AssemblyQualifiedName!,
            });

        var (recorder, consumer) = await Run(sp, input);

        Assert.True(recorder.CompleteCalled);
        Assert.Equal("inline", consumer.Received?.Payload);
        Assert.Equal(0, store.GetCount);
    }

    [Fact]
    public async Task Missing_Claim_Check_Blob_Dead_Letters()
    {
        var store = new FakeStore();
        var sp = BuildSp(store);

        // Ref header points at a payload that was never stored (reaped).
        var input = new AzureServiceBusMessageBus.IncomingAsbMessage(
            Body: "{\"__claimcheck__\":true}",
            MessageId: Guid.NewGuid().ToString(),
            DeliveryCount: 1,
            ApplicationProperties: new Dictionary<string, object>
            {
                ["MessageType"] = typeof(TestMessage).AssemblyQualifiedName!,
                [ClaimCheck.HeaderKey] = Guid.NewGuid().ToString("D"),
            });

        var (recorder, consumer) = await Run(sp, input);

        Assert.Equal(0, consumer.DispatchCount);
        Assert.False(recorder.CompleteCalled);
        Assert.False(recorder.AbandonCalled);
        Assert.Equal("ClaimCheckMissing", recorder.DeadLetterReason);
    }
}
