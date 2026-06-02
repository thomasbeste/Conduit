namespace Conduit.Messaging;

/// <summary>
/// A single message consumer whose liveness is owned by a transport-agnostic
/// <see cref="ConsumerSupervisor"/> rather than by the underlying client
/// library's best-effort auto-recovery.
///
/// The durability contract lives here, in the abstraction — not in any one
/// provider — so every transport gets the SAME guarantee: a consumer that is
/// not currently consuming WILL be rebuilt, for as long as the process runs,
/// no matter how long the broker was gone. Before this seam existed, durability
/// was an accident of the provider: Azure Service Bus inherited it from the
/// SDK's self-supervising <c>ServiceBusProcessor</c>, while the hand-rolled
/// RabbitMQ host gave up after a bounded number of channel-open attempts and
/// never recreated a permanently-dead connection — so a long broker crashloop
/// stranded every consumer until the pod was manually restarted.
///
/// Implementations supply exactly two primitives; the supervisor supplies the
/// loop:
///   * <see cref="IsHealthy"/> — cheap, non-throwing liveness probe.
///   * <see cref="EnsureRunningAsync"/> — idempotent "start, or rebuild from
///     ANY dead state (connection included)". Safe to call repeatedly.
/// </summary>
public interface ISupervisedConsumer : IAsyncDisposable
{
    /// <summary>Stable, human-readable name for logs/metrics (e.g. the queue or subscription).</summary>
    string Name { get; }

    /// <summary>
    /// True iff this consumer is, right now, connected to the broker AND actively
    /// receiving deliveries. Must never throw — a probe failure counts as unhealthy.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Bring the consumer to a running state, rebuilding whatever is dead —
    /// transport connection, channel/link, subscription, and the consume loop.
    /// Idempotent: a call when already healthy is a no-op. May be called many
    /// times over the process lifetime. Throwing is acceptable (the supervisor
    /// will back off and retry); it must not leave the consumer in a state that
    /// reports <see cref="IsHealthy"/> = true while not actually consuming.
    /// </summary>
    Task EnsureRunningAsync(CancellationToken cancellationToken);
}
