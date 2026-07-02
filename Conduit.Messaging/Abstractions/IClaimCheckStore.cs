namespace Conduit.Messaging;

/// <summary>
/// Backing store for the transport-level claim-check (Conduit stays generic —
/// the host app provides the persistence). When registered, the bus transparently
/// offloads any message body over <see cref="ClaimCheck.OffloadAsync"/>'s threshold
/// here on publish and rehydrates it on consume, so a body can never exceed the
/// broker's per-message size limit (256 KB on Azure Service Bus).
///
/// Deliberately minimal — Store + Get only. The transport NEVER deletes on
/// consume (a delete before message-complete would strand a redelivery); the host
/// reaps rows on a TTL sweep instead.
/// </summary>
public interface IClaimCheckStore
{
    /// <summary>Persist an offloaded body and return its opaque reference id.</summary>
    Task<Guid> StoreAsync(ReadOnlyMemory<byte> payload, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Fetch a body by reference; null if it was reaped or never stored.</summary>
    Task<byte[]?> GetAsync(Guid payloadId, CancellationToken cancellationToken = default);
}
