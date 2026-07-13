using System.Collections.Concurrent;
using System.Diagnostics;
using Conduit.Messaging.RabbitMq;
using Conduit.Messaging.RabbitMq.Registration;
using Conduit.Messaging.Registration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.RabbitMq;

namespace Conduit.Messaging.IntegrationTests;

/// <summary>
/// Real-broker tests for the Conduit RabbitMQ transport: an actual
/// publish -> RabbitMQ -> consume roundtrip, and the durability guarantee —
/// after the broker is taken down and brought back, consumers self-heal and
/// resume with NO manual restart (the behaviour added in Conduit #18/#19).
///
/// Requires Docker; kept out of the fast unit suite (Conduit.Messaging.Tests).
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
        .WithUsername("conduit")
        .WithPassword("conduit")
        .WithPortBinding(15672, true)
        .Build();

    public ValueTask InitializeAsync() => new(Container.StartAsync());

    public ValueTask DisposeAsync() => Container.DisposeAsync();
}

public sealed class RabbitMqTransportTests(RabbitMqFixture fixture) : IClassFixture<RabbitMqFixture>
{
    public sealed record Ping(string Value);
    public sealed record OversizedPing(string Value);

    public sealed class PingConsumer : IMessageConsumer<Ping>
    {
        // The work is observed via the bus's OnMessageConsumed hook (fires after
        // a successful ack), so the handler itself only needs to succeed.
        public Task ConsumeAsync(Ping message, MessageContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    public sealed class OversizedPingConsumer : IMessageConsumer<OversizedPing>
    {
        public Task ConsumeAsync(
            OversizedPing message,
            MessageContext context,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingClaimCheckStore : IClaimCheckStore
    {
        private sealed record StoredPayload(byte[] Body, int ExpectedConsumers)
        {
            public HashSet<string> ReleasedConsumers { get; } = new(StringComparer.Ordinal);
        }

        private readonly ConcurrentDictionary<Guid, StoredPayload> _payloads = new();

        public int? LastExpectedConsumers { get; private set; }
        public int Count => _payloads.Count;

        public Task<Guid> StoreAsync(
            ReadOnlyMemory<byte> payload,
            string contentType,
            int expectedConsumers,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            LastExpectedConsumers = expectedConsumers;
            Assert.True(_payloads.TryAdd(id, new StoredPayload(payload.ToArray(), expectedConsumers)));
            return Task.FromResult(id);
        }

        public Task<byte[]?> GetAsync(Guid payloadId, CancellationToken cancellationToken = default)
            => Task.FromResult(_payloads.TryGetValue(payloadId, out var payload) ? payload.Body : null);

        public Task ReleaseAsync(
            Guid payloadId,
            string consumerId,
            CancellationToken cancellationToken = default)
        {
            if (!_payloads.TryGetValue(payloadId, out var payload))
                return Task.CompletedTask;

            lock (payload.ReleasedConsumers)
            {
                payload.ReleasedConsumers.Add(consumerId);
                if (payload.ReleasedConsumers.Count >= payload.ExpectedConsumers)
                    _payloads.TryRemove(payloadId, out _);
            }

            return Task.CompletedTask;
        }
    }

    private async Task<(ServiceProvider Sp, IMessageBus Bus, ConcurrentBag<string> Consumed)> BuildBusAsync(
        string serviceName,
        CancellationToken ct,
        IClaimCheckStore? claimCheckStore = null,
        bool consumeOversizedPing = false)
    {
        var consumed = new ConcurrentBag<string>();

        var services = new ServiceCollection();
        services.AddLogging();
        if (claimCheckStore is not null)
            services.AddSingleton(claimCheckStore);
        services.AddConduitMessaging(cfg =>
        {
            cfg.ServiceName = serviceName;
            cfg.UseRabbitMq(s =>
            {
                s.Host = fixture.Container.Hostname;
                s.Port = fixture.Container.GetMappedPublicPort(5672);
                s.Username = "conduit";
                s.Password = "conduit";
                s.VirtualHost = "/";
                s.ManagementUrl = $"http://{fixture.Container.Hostname}:{fixture.Container.GetMappedPublicPort(15672)}";
                s.RetryCount = 3;
            });
            if (consumeOversizedPing)
                cfg.AddConsumer<OversizedPingConsumer>();
            else
                cfg.AddConsumer<PingConsumer>();
        });

        var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IMessageBus>();

        // Observe consumption (set before Start; the host reads it lazily).
        if (bus is RabbitMqMessageBus rmq)
            rmq.OnMessageConsumed = (msg, _) =>
            {
                if (msg is Ping ping) consumed.Add(ping.Value);
                if (msg is OversizedPing oversized) consumed.Add(oversized.Value);
            };

        await bus.StartAsync(ct);
        return (sp, bus, consumed);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    /// <summary>
    /// Publish repeatedly until the value is consumed or the timeout elapses.
    /// Used across a broker restart, where the first publishes may fail while the
    /// publisher's connection is still being recreated — the retry rides that out.
    /// </summary>
    private static async Task<bool> PublishUntilConsumedAsync(
        IMessageBus bus, ConcurrentBag<string> consumed, string value, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { await bus.Publisher.PublishAsync(new Ping(value), ct); }
            catch { /* broker still recovering — retry on the next loop */ }

            if (await WaitUntilAsync(() => consumed.Contains(value), TimeSpan.FromSeconds(5)))
                return true;
        }
        return consumed.Contains(value);
    }

    [Fact]
    public async Task Publishes_and_consumes_through_a_real_broker()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sp, bus, consumed) = await BuildBusAsync($"rt-{Guid.NewGuid():N}", ct);
        await using var _ = sp;

        await bus.Publisher.PublishAsync(new Ping("hello"), ct);

        Assert.True(
            await WaitUntilAsync(() => consumed.Contains("hello"), TimeSpan.FromSeconds(30)),
            "the consumer should receive the message through the real RabbitMQ broker");
    }

    [Fact]
    public async Task Oversized_fanout_payload_is_released_after_both_real_queues_acknowledge()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingClaimCheckStore();
        var (firstProvider, firstBus, firstConsumed) = await BuildBusAsync(
            $"fanout-a-{Guid.NewGuid():N}", ct, store, consumeOversizedPing: true);
        await using var _first = firstProvider;
        var (secondProvider, _, secondConsumed) = await BuildBusAsync(
            $"fanout-b-{Guid.NewGuid():N}", ct, store, consumeOversizedPing: true);
        await using var _second = secondProvider;
        var value = new string('x', ClaimCheck.DefaultThresholdBytes + 1);

        await firstBus.Publisher.PublishAsync(new OversizedPing(value), ct);

        Assert.True(
            await WaitUntilAsync(
                () => firstConsumed.Contains(value) && secondConsumed.Contains(value),
                TimeSpan.FromSeconds(30)),
            "both durable queues should consume the same oversized fan-out delivery");
        Assert.Equal(2, store.LastExpectedConsumers);
        Assert.True(
            await WaitUntilAsync(() => store.Count == 0, TimeSpan.FromSeconds(10)),
            "the last distinct queue acknowledgement should delete the offloaded payload");
    }

    [Fact]
    public async Task Consumer_self_heals_after_a_broker_restart_with_no_manual_action()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sp, bus, consumed) = await BuildBusAsync($"resil-{Guid.NewGuid():N}", ct);
        await using var _ = sp;

        // Baseline: it consumes before the outage.
        await bus.Publisher.PublishAsync(new Ping("before"), ct);
        Assert.True(
            await WaitUntilAsync(() => consumed.Contains("before"), TimeSpan.FromSeconds(30)),
            "baseline: the consumer should be consuming before the broker outage");

        // Simulate broker loss WITHOUT changing the mapped port (a real pod
        // restart would re-map the port and trivially reconnect): stop then start
        // the RabbitMQ application inside the running container. Connections drop;
        // durable queues persist in mnesia.
        var stop = await fixture.Container.ExecAsync(["rabbitmqctl", "stop_app"], ct);
        Assert.Equal(0, stop.ExitCode);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var start = await fixture.Container.ExecAsync(["rabbitmqctl", "start_app"], ct);
        Assert.Equal(0, start.ExitCode);

        // The critical assertion: with NO manual restart of the bus or consumer,
        // the supervisor reconnects and the consumer resumes processing new
        // messages. Before #18 this hung forever (consumer stranded).
        Assert.True(
            await PublishUntilConsumedAsync(bus, consumed, "after", TimeSpan.FromSeconds(90), ct),
            "after a broker restart the consumer must self-heal and consume new messages with no manual action");
    }
}
