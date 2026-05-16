namespace Conduit.Messaging;

/// <summary>
/// Transport-agnostic admin operations on a single subscription / queue.
/// Companion to <see cref="IMessagingStatsProvider"/> — stats tells you
/// what's stuck, this lets you do something about it without SSH'ing into
/// the cloud control plane.
///
/// The 2026-05-15 silent-zombie incident is the cautionary tale: 19 stuck
/// messages × 3 subscriptions held replicas alive at the queue-depth scaler
/// for 33 hours. The only way to stop the bleed was an Azure portal admin
/// purging the subs manually. A customer would not have that option — the
/// admin panel needs to expose these operations directly.
/// </summary>
public interface IMessagingAdmin
{
    /// <summary>
    /// Drop every active message on the named subscription / queue.
    /// Implementations may achieve this by delete-and-recreate (ASB) or
    /// by purging the queue contents (RabbitMQ); the post-condition is
    /// "active count = 0" either way. Subscriptions remain bound to the
    /// topic with the same settings.
    /// </summary>
    Task<long> PurgeActiveAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Drop every message in the dead-letter sub-queue of the named
    /// subscription / queue. Used after diagnosing why messages failed —
    /// once the cause is fixed and you've decided the in-flight DLQ
    /// payloads are not worth replaying.
    /// </summary>
    Task<long> PurgeDeadLetterAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Move every message in the dead-letter sub-queue back to the active
    /// queue so the consumer gets another shot. Cap the operation at
    /// <paramref name="max"/> messages to avoid replaying a million-row
    /// DLQ in one go.
    /// </summary>
    Task<long> RedeliverDeadLetterAsync(string name, int max, CancellationToken ct = default);
}
