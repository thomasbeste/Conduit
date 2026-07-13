namespace Conduit.Messaging;

/// <summary>
/// Transport-level claim-check codec (#2432). A single code path both the Azure
/// Service Bus and RabbitMQ publishers/consumers call, so dev (RabbitMQ) and prod
/// (ASB) behave identically: on publish, a body over <see cref="DefaultThresholdBytes"/>
/// is stored via <see cref="IClaimCheckStore"/> and replaced with a tiny placeholder
/// carrying the reference in a reserved header; on consume, that reference is fetched
/// back and swapped in before deserialization — transparently, so message contracts
/// and handlers are untouched.
///
/// No-op when no <see cref="IClaimCheckStore"/> is registered (Conduit stays usable
/// without the feature).
/// </summary>
public static class ClaimCheck
{
    private const int ReleaseAttempts = 3;
    private static readonly TimeSpan ReleaseRetryDelay = TimeSpan.FromMilliseconds(100);
    /// <summary>
    /// Reserved message header (ASB application property / RabbitMQ envelope header)
    /// carrying the claim-check reference. Not <c>ctx-</c>-prefixed: it is a transport
    /// concern, not application context baggage.
    /// </summary>
    public const string HeaderKey = "claimcheck-ref";

    /// <summary>
    /// Offload threshold. 200 KB leaves headroom under the 256 KB ASB hard limit for
    /// headers + the AMQP framing overhead the broker measures on top of the body.
    /// </summary>
    public const int DefaultThresholdBytes = 200 * 1024;

    /// <summary>Placeholder body sent in place of an offloaded payload.</summary>
    private static readonly byte[] Placeholder =
        System.Text.Encoding.UTF8.GetBytes("{\"__claimcheck__\":true}");

    /// <summary>Result of an offload attempt: the body to send + the reference to header (null when not offloaded).</summary>
    public readonly record struct OffloadResult(ReadOnlyMemory<byte> Body, Guid? Reference);

    /// <summary>Result of rehydration, including the reference retained until broker settlement.</summary>
    public readonly record struct RehydrateResult(ReadOnlyMemory<byte> Body, Guid? Reference);

    /// <summary>
    /// If a store is configured and <paramref name="body"/> exceeds <paramref name="thresholdBytes"/>,
    /// persist it and return the placeholder body + its reference. Otherwise return the body unchanged.
    /// </summary>
    public static async Task<OffloadResult> OffloadAsync(
        ReadOnlyMemory<byte> body,
        string contentType,
        IClaimCheckStore? store,
        int thresholdBytes = DefaultThresholdBytes,
        int expectedConsumers = 1,
        CancellationToken cancellationToken = default)
    {
        if (store is null || body.Length <= thresholdBytes)
            return new OffloadResult(body, null);

        if (expectedConsumers < 1)
            throw new ArgumentOutOfRangeException(
                nameof(expectedConsumers), expectedConsumers, "At least one routed consumer is required.");

        var reference = await store.StoreAsync(body, contentType, expectedConsumers, cancellationToken);
        return new OffloadResult(Placeholder, reference);
    }

    /// <summary>
    /// If <paramref name="tryGetHeader"/> yields a claim-check reference, fetch the original
    /// body and return it; otherwise return <paramref name="body"/> unchanged. Throws
    /// <see cref="ClaimCheckMissingException"/> when the reference is present but the blob is
    /// gone (already consumed/never-stored) — the caller must dead-letter, since retrying can't help.
    /// </summary>
    public static async Task<ReadOnlyMemory<byte>> RehydrateAsync(
        ReadOnlyMemory<byte> body,
        Func<string, string?> tryGetHeader,
        IClaimCheckStore? store,
        CancellationToken cancellationToken = default)
        => (await RehydrateWithReferenceAsync(body, tryGetHeader, store, cancellationToken)).Body;

    /// <summary>
    /// Rehydrate a body and retain its parsed reference so the transport can release
    /// it after successful broker settlement without parsing transport headers twice.
    /// </summary>
    public static async Task<RehydrateResult> RehydrateWithReferenceAsync(
        ReadOnlyMemory<byte> body,
        Func<string, string?> tryGetHeader,
        IClaimCheckStore? store,
        CancellationToken cancellationToken = default)
    {
        var reference = tryGetHeader(HeaderKey);
        if (string.IsNullOrEmpty(reference))
            return new RehydrateResult(body, null);

        if (store is null)
            throw new InvalidOperationException(
                "Message carries a claim-check reference but no IClaimCheckStore is registered.");
        if (!Guid.TryParse(reference, out var payloadId))
            throw new InvalidOperationException($"Malformed claim-check reference: '{reference}'.");

        var fetched = await store.GetAsync(payloadId, cancellationToken);
        if (fetched is null)
            throw new ClaimCheckMissingException(payloadId);

        return new RehydrateResult(fetched, payloadId);
    }

    /// <summary>
    /// Settle a successfully dispatched broker delivery, then idempotently release
    /// that subscription/queue's ownership. The store deletes after every routed
    /// owner releases. A settlement failure leaves the body untouched for redelivery;
    /// release is retried three times, then a persistent failure is wrapped so hosts
    /// never abandon or nack an already-settled delivery.
    /// </summary>
    public static async Task SettleAndReleaseAsync(
        Guid? reference,
        IClaimCheckStore? store,
        string consumerId,
        Func<CancellationToken, Task> settle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settle);

        await settle(cancellationToken);

        if (reference is not Guid payloadId)
            return;

        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ClaimCheckCleanupException(
                payloadId,
                new ArgumentException("A stable subscription or queue id is required.", nameof(consumerId)));

        if (store is null)
            throw new ClaimCheckCleanupException(
                payloadId,
                new InvalidOperationException(
                    "Message was rehydrated from claim-check but no IClaimCheckStore is available for cleanup."));

        Exception? releaseFailure = null;
        for (var attempt = 1; attempt <= ReleaseAttempts; attempt++)
        {
            try
            {
                // The broker delivery token may be cancelled as soon as settlement
                // completes. Ownership release is post-settlement durable cleanup and
                // must not be skipped merely because that delivery scope has ended.
                await store.ReleaseAsync(payloadId, consumerId, CancellationToken.None);
                return;
            }
            catch (Exception ex) when (ex is not ClaimCheckCleanupException)
            {
                releaseFailure = ex;
                if (attempt < ReleaseAttempts)
                    await Task.Delay(ReleaseRetryDelay * attempt, CancellationToken.None);
            }
        }

        throw new ClaimCheckCleanupException(payloadId, releaseFailure!);
    }
}

/// <summary>
/// A message carried a claim-check reference whose payload is gone (already consumed or
/// never stored). Unrecoverable — the consumer dead-letters rather than retries.
/// </summary>
public sealed class ClaimCheckMissingException(Guid payloadId)
    : Exception($"Claim-check payload {payloadId:D} not found (already consumed or never stored); dead-lettering.")
{
    public Guid PayloadId { get; } = payloadId;
}

/// <summary>
/// Broker settlement succeeded but deleting the corresponding claim-check body
/// failed. The delivery must not be retried because the broker already considers
/// it complete; hosts surface this failure without nacking or abandoning.
/// </summary>
public sealed class ClaimCheckCleanupException(Guid payloadId, Exception innerException)
    : Exception($"Settled message but failed to release claim-check payload {payloadId:D}.", innerException)
{
    public Guid PayloadId { get; } = payloadId;
}
