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

    /// <summary>
    /// If a store is configured and <paramref name="body"/> exceeds <paramref name="thresholdBytes"/>,
    /// persist it and return the placeholder body + its reference. Otherwise return the body unchanged.
    /// </summary>
    public static async Task<OffloadResult> OffloadAsync(
        ReadOnlyMemory<byte> body,
        string contentType,
        IClaimCheckStore? store,
        int thresholdBytes = DefaultThresholdBytes,
        CancellationToken cancellationToken = default)
    {
        if (store is null || body.Length <= thresholdBytes)
            return new OffloadResult(body, null);

        var reference = await store.StoreAsync(body, contentType, cancellationToken);
        return new OffloadResult(Placeholder, reference);
    }

    /// <summary>
    /// If <paramref name="tryGetHeader"/> yields a claim-check reference, fetch the original
    /// body and return it; otherwise return <paramref name="body"/> unchanged. Throws
    /// <see cref="ClaimCheckMissingException"/> when the reference is present but the blob is
    /// gone (reaped/never-stored) — the caller must dead-letter, since retrying can't help.
    /// </summary>
    public static async Task<ReadOnlyMemory<byte>> RehydrateAsync(
        ReadOnlyMemory<byte> body,
        Func<string, string?> tryGetHeader,
        IClaimCheckStore? store,
        CancellationToken cancellationToken = default)
    {
        var reference = tryGetHeader(HeaderKey);
        if (string.IsNullOrEmpty(reference))
            return body;

        if (store is null)
            throw new InvalidOperationException(
                "Message carries a claim-check reference but no IClaimCheckStore is registered.");
        if (!Guid.TryParse(reference, out var payloadId))
            throw new InvalidOperationException($"Malformed claim-check reference: '{reference}'.");

        var fetched = await store.GetAsync(payloadId, cancellationToken);
        if (fetched is null)
            throw new ClaimCheckMissingException(payloadId);

        return fetched;
    }
}

/// <summary>
/// A message carried a claim-check reference whose payload is gone (TTL-reaped or
/// never stored). Unrecoverable — the consumer dead-letters rather than retries.
/// </summary>
public sealed class ClaimCheckMissingException(Guid payloadId)
    : Exception($"Claim-check payload {payloadId:D} not found (reaped or never stored); dead-lettering.")
{
    public Guid PayloadId { get; } = payloadId;
}
