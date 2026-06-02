using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Messaging.Tests;

/// <summary>
/// Proves the transport-agnostic durability contract: the supervisor brings
/// consumers up, heals one that dies, and never gives up while the broker is
/// gone — recovering the moment it returns. This is exactly the guarantee that
/// was missing on the hand-rolled RabbitMQ path (2026-06-02 stranded consumers)
/// and is now enforced for EVERY transport via <see cref="ISupervisedConsumer"/>.
/// </summary>
public class ConsumerSupervisorTests
{
    private sealed class FakeConsumer : ISupervisedConsumer
    {
        private int _healthy;
        private int _ensureCalls;

        /// <summary>While true, EnsureRunning throws — simulates an unreachable broker.</summary>
        public volatile bool BrokerDown;

        public string Name => "fake";
        public bool IsHealthy => Volatile.Read(ref _healthy) == 1;
        public int EnsureCalls => Volatile.Read(ref _ensureCalls);

        /// <summary>Simulate the consumer silently dying (channel/link dropped).</summary>
        public void Kill() => Volatile.Write(ref _healthy, 0);

        public Task EnsureRunningAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _ensureCalls);
            if (BrokerDown)
                throw new InvalidOperationException("broker unreachable");
            Volatile.Write(ref _healthy, 1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Brings_consumers_up_on_start()
    {
        var consumer = new FakeConsumer();
        await using var supervisor = new ConsumerSupervisor(
            new ISupervisedConsumer[] { consumer }, NullLogger.Instance, TimeSpan.FromMilliseconds(40));

        await supervisor.StartAsync(CancellationToken.None);

        Assert.True(consumer.IsHealthy);
        Assert.Equal(1, consumer.EnsureCalls);
    }

    [Fact]
    public async Task Heals_a_consumer_that_dies_silently()
    {
        var consumer = new FakeConsumer();
        await using var supervisor = new ConsumerSupervisor(
            new ISupervisedConsumer[] { consumer }, NullLogger.Instance, TimeSpan.FromMilliseconds(40));
        await supervisor.StartAsync(CancellationToken.None);

        // The consumer dies with no event/notification — the only signal is that
        // it stops being healthy. The poll-based supervisor must still detect it.
        consumer.Kill();

        Assert.True(await WaitUntilAsync(() => consumer.IsHealthy, TimeSpan.FromSeconds(3)),
            "supervisor should rebuild a consumer that died with no notification");
        Assert.True(consumer.EnsureCalls >= 2);
    }

    [Fact]
    public async Task Never_gives_up_and_recovers_when_the_broker_returns()
    {
        var consumer = new FakeConsumer { BrokerDown = true };
        await using var supervisor = new ConsumerSupervisor(
            new ISupervisedConsumer[] { consumer }, NullLogger.Instance, TimeSpan.FromMilliseconds(40));

        // Initial bring-up fails (broker down) but must not throw out of Start.
        await supervisor.StartAsync(CancellationToken.None);
        Assert.False(consumer.IsHealthy);

        // While the broker stays down, the supervisor keeps retrying — no cap.
        Assert.True(await WaitUntilAsync(() => consumer.EnsureCalls >= 3, TimeSpan.FromSeconds(3)),
            "supervisor must keep retrying a down broker, not give up");
        Assert.False(consumer.IsHealthy);

        // Broker returns — the very next probe heals it, with no manual action.
        consumer.BrokerDown = false;
        Assert.True(await WaitUntilAsync(() => consumer.IsHealthy, TimeSpan.FromSeconds(3)),
            "supervisor should recover automatically once the broker is reachable again");
    }
}
