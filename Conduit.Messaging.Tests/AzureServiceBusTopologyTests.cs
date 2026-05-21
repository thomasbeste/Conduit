using Conduit.Messaging;
using Conduit.Messaging.AzureServiceBus;
using Conduit.Messaging.Registration;
using Xunit;

namespace Conduit.Messaging.Tests;

/// <summary>
/// Verify-or-throw behaviour for the topology contract: topic + every
/// per-service subscription must pre-exist on the broker. Lazy auto-create
/// was removed (silent-zombie incident, 2026-05-15). This pure-logic test
/// drives VerifyTopologyAsync directly with lambdas so we don't need a real
/// ASB / Azurite / mock framework to exercise both failure paths.
/// </summary>
public class AzureServiceBusTopologyTests
{
    private const string TopicName = "gpi-events";
    private const string ServiceName = "service-pii";

    private static readonly List<ConsumerRegistration> OneConsumer =
    [
        new() { ConsumerType = typeof(FakeConsumer), MessageType = typeof(FakeMessage) },
    ];

    [Fact]
    public async Task Succeeds_When_Topic_And_All_Subscriptions_Exist()
    {
        await AzureServiceBusMessageBus.VerifyTopologyAsync(
            topicExists: (name, _) => Task.FromResult(name == TopicName),
            subscriptionExists: (topic, sub, _) => Task.FromResult(
                topic == TopicName
                && sub == AzureServiceBusMessageBus.BuildSubscriptionName(ServiceName, nameof(FakeMessage))),
            topicName: TopicName,
            serviceName: ServiceName,
            consumerRegistrations: OneConsumer,
            ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Throws_When_Topic_Missing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AzureServiceBusMessageBus.VerifyTopologyAsync(
                topicExists: (_, _) => Task.FromResult(false),
                subscriptionExists: (_, _, _) => Task.FromResult(true), // wouldn't reach here
                topicName: TopicName,
                serviceName: ServiceName,
                consumerRegistrations: OneConsumer,
                ct: TestContext.Current.CancellationToken));
        Assert.Contains(TopicName, ex.Message);
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("Bicep", ex.Message); // points operator at infra
    }

    [Fact]
    public async Task Throws_When_Subscription_Missing()
    {
        var expected = AzureServiceBusMessageBus.BuildSubscriptionName(ServiceName, nameof(FakeMessage));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AzureServiceBusMessageBus.VerifyTopologyAsync(
                topicExists: (_, _) => Task.FromResult(true),
                subscriptionExists: (_, _, _) => Task.FromResult(false),
                topicName: TopicName,
                serviceName: ServiceName,
                consumerRegistrations: OneConsumer,
                ct: TestContext.Current.CancellationToken));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("post-update runner", ex.Message); // points operator at deploy pipeline
    }

    [Fact]
    public async Task Throws_On_The_First_Missing_Subscription()
    {
        // Two consumers — first subscription exists, second doesn't.
        var regs = new List<ConsumerRegistration>
        {
            new() { ConsumerType = typeof(FakeConsumer), MessageType = typeof(FakeMessage) },
            new() { ConsumerType = typeof(FakeConsumer2), MessageType = typeof(FakeMessage2) },
        };
        var missing = AzureServiceBusMessageBus.BuildSubscriptionName(ServiceName, nameof(FakeMessage2));
        var present = AzureServiceBusMessageBus.BuildSubscriptionName(ServiceName, nameof(FakeMessage));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AzureServiceBusMessageBus.VerifyTopologyAsync(
                topicExists: (_, _) => Task.FromResult(true),
                subscriptionExists: (_, sub, _) => Task.FromResult(sub == present),
                topicName: TopicName,
                serviceName: ServiceName,
                consumerRegistrations: regs,
                ct: TestContext.Current.CancellationToken));
        // Error names the missing subscription specifically — operator can
        // identify which consumer's manifest is the problem.
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public async Task Succeeds_When_No_Consumers_Are_Registered()
    {
        // A service with only publishers (no subscribe()) still needs the
        // topic to exist but has no subs to verify. Should not throw.
        await AzureServiceBusMessageBus.VerifyTopologyAsync(
            topicExists: (_, _) => Task.FromResult(true),
            subscriptionExists: (_, _, _) => Task.FromResult(false), // wouldn't be called
            topicName: TopicName,
            serviceName: ServiceName,
            consumerRegistrations: [],
            ct: TestContext.Current.CancellationToken);
    }

    // --- Test types --------------------------------------------------------

    private sealed class FakeMessage { }
    private sealed class FakeMessage2 { }
    private sealed class FakeConsumer : IMessageConsumer<FakeMessage>
    {
        public Task ConsumeAsync(FakeMessage message, MessageContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FakeConsumer2 : IMessageConsumer<FakeMessage2>
    {
        public Task ConsumeAsync(FakeMessage2 message, MessageContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
