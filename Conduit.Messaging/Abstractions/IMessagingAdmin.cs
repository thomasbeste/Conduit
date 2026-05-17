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
    Task<DrainResult> PurgeActiveAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Drop every message in the dead-letter sub-queue of the named
    /// subscription / queue. Used after diagnosing why messages failed —
    /// once the cause is fixed and you've decided the in-flight DLQ
    /// payloads are not worth replaying.
    ///
    /// Loops until the DLQ is empty or an internal admin-op timeout
    /// fires (caller-passed cancellation is honored). Returns a
    /// <see cref="DrainResult"/> with both how many messages were
    /// dropped this call and how many remain, so the UI can show "done"
    /// vs "still N — retry once locks expire" without re-querying.
    /// </summary>
    Task<DrainResult> PurgeDeadLetterAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Move every message in the dead-letter sub-queue back to the active
    /// queue so the consumer gets another shot. Cap the operation at
    /// <paramref name="max"/> messages to avoid replaying a million-row
    /// DLQ in one go.
    /// </summary>
    Task<DrainResult> RedeliverDeadLetterAsync(string name, int max, CancellationToken ct = default);
}

/// <summary>
/// Result of a drain/redeliver/purge admin operation.
///
/// <c>Drained</c> is how many messages this call processed.
/// <c>Remaining</c> is the authoritative broker-side count after the
/// call returns — non-zero means the call hit its internal timeout (for
/// ASB DLQ drains, usually because of locks held from earlier partial
/// drains that haven't expired). The UI uses this to decide whether to
/// show "done" or "still N — retry shortly".
/// </summary>
public readonly record struct DrainResult(long Drained, long Remaining);
