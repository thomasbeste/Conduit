namespace Conduit.Messaging;

/// <summary>
/// Backing store for the transport-level claim-check (Conduit stays generic —
/// the host app provides the persistence). When registered, the bus transparently
/// offloads any message body over <see cref="ClaimCheck.OffloadAsync"/>'s threshold
/// here on publish and rehydrates it on consume, so a body can never exceed the
/// broker's per-message size limit (256 KB on Azure Service Bus).
///
/// The transport deletes an offloaded body only after the consumer has returned
/// successfully and the broker has acknowledged/completed the delivery. Deleting
/// before broker settlement would strand a redelivery if the channel or lock were
/// lost between dispatch and settlement.
/// </summary>
public interface IClaimCheckStore
{
    /// <summary>Persist an offloaded body and return its opaque reference id.</summary>
    Task<Guid> StoreAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        int expectedConsumers,
        CancellationToken cancellationToken = default);

    /// <summary>Fetch a body by reference; null if it was already consumed or never stored.</summary>
    Task<byte[]?> GetAsync(Guid payloadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently release one subscription/queue owner's claim after successful
    /// broker settlement. The store deletes the body only after every routed owner
    /// has released it. Implementations must propagate persistence failures so the
    /// host can surface a post-settlement cleanup fault without retrying an
    /// already-settled delivery.
    /// </summary>
    Task ReleaseAsync(
        Guid payloadId,
        string consumerId,
        CancellationToken cancellationToken = default);
}
