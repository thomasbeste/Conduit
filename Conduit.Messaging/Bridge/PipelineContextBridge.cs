using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Conduit.Mediator;

namespace Conduit.Messaging.Bridge;

/// <summary>
/// Bridges <see cref="IPipelineContext"/> (in-process mediator) with <see cref="MessageContext"/> (cross-process messaging).
/// Extracts baggage and causality from the pipeline context into message headers on publish,
/// and hydrates a pipeline context from message headers on consume.
/// </summary>
public static class PipelineContextBridge
{
    private const string BaggagePrefix = "conduit.baggage.";
    private const string CausalityChainHeader = "conduit.causality-chain";
    private const string OriginRequestIdHeader = "conduit.origin-request-id";
    private const string CorrelationIdHeader = "conduit.correlation-id";

    /// <summary>
    /// Header carrying the base64 HMAC-SHA256 over the canonical JSON of
    /// the identity-bearing subset of baggage (see <see cref="SignedBaggageKeys"/>).
    /// Consumers refuse to hydrate when any signed key is present but this
    /// header is missing or doesn't match — see #897.
    /// </summary>
    public const string SignatureHeader = "conduit.baggage.sig";

    /// <summary>
    /// Subset of baggage keys whose values are security-critical (they drive
    /// authorisation on the consumer side) and therefore HMAC-signed on the
    /// publish side. Other baggage keys (correlation_id, feature flags, etc.)
    /// are diagnostic and ride along unsigned.
    /// </summary>
    public static readonly IReadOnlyList<string> SignedBaggageKeys =
    [
        "tenant_id",
        "user_id",
        "user_role",
        "group_ids",
        "session_id",
    ];

    private static readonly HashSet<string> SignedBaggageKeySet =
        new(SignedBaggageKeys, StringComparer.Ordinal);

    /// <summary>
    /// Extracts pipeline context state (baggage, causality, correlation) into headers
    /// suitable for transport via message broker.
    /// </summary>
    /// <param name="context">Source pipeline context.</param>
    /// <param name="signingKey">HMAC key used to sign the identity baggage subset. When any
    /// signed key is present in baggage this MUST be non-null — otherwise the publisher
    /// would ship forgeable identity. Throws <see cref="InvalidOperationException"/> on
    /// misconfiguration to fail loud instead of silently dropping the signature.</param>
    public static Dictionary<string, string> ExtractHeaders(IPipelineContext context, IMessagingSigningKey? signingKey = null)
    {
        var headers = new Dictionary<string, string>();

        // Extract baggage
        var baggage = context.GetAllBaggage();
        var signedSubset = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in baggage)
        {
            headers[$"{BaggagePrefix}{key}"] = value;
            if (SignedBaggageKeySet.Contains(key))
            {
                signedSubset[key] = value;
            }
        }

        if (signedSubset.Count > 0)
        {
            if (signingKey is null)
            {
                throw new InvalidOperationException(
                    "PipelineContextBridge.ExtractHeaders was called with identity-bearing baggage " +
                    $"({string.Join(", ", signedSubset.Keys)}) but no IMessagingSigningKey. Identity " +
                    "would cross the bus unsigned and forgeable. Register an IMessagingSigningKey.");
            }
            headers[SignatureHeader] = ComputeSignature(signedSubset, signingKey.GetKey());
        }

        // Extract current request ID as the origin of this message
        var currentRequestId = context.GetCurrentRequestId();
        if (currentRequestId is not null)
        {
            headers[OriginRequestIdHeader] = currentRequestId;
        }

        // Extract causality chain (serialized as pipe-delimited entries)
        var chain = context.GetCausalityChain();
        if (chain.Count > 0)
        {
            headers[CausalityChainHeader] = string.Join("|",
                chain.Select(e => $"{e.RequestId};{e.ParentId ?? ""};{e.RequestType};{e.Timestamp:O}"));
        }

        // Propagate correlation ID from baggage if present
        var correlationId = context.GetBaggage("correlation_id") ?? context.GetBaggage("request_id");
        if (correlationId is not null)
        {
            headers[CorrelationIdHeader] = correlationId;
        }

        return headers;
    }

    /// <summary>
    /// Hydrates a pipeline context with state from incoming message headers.
    /// Call this early in the consumer pipeline to restore cross-process context.
    /// </summary>
    /// <param name="signingKey">HMAC key used to verify the identity baggage subset.
    /// When any signed-subset baggage key arrives in the headers this MUST be non-null;
    /// the bridge throws <see cref="IdentitySignatureMismatchException"/> when the
    /// signature is missing, malformed, or doesn't verify — the consumer host then
    /// DLQs the message rather than dispatching forged identity.</param>
    public static void HydrateContext(IPipelineContext context, MessageContext messageContext, IMessagingSigningKey? signingKey = null)
    {
        if (messageContext.Headers is null) return;

        VerifySignatureOrThrow(messageContext.Headers, messageContext.MessageId, signingKey);

        // Restore baggage
        foreach (var (key, value) in messageContext.Headers)
        {
            if (key.StartsWith(BaggagePrefix, StringComparison.Ordinal))
            {
                if (string.Equals(key, SignatureHeader, StringComparison.Ordinal))
                    continue;
                var baggageKey = key[BaggagePrefix.Length..];
                context.SetBaggage(baggageKey, value);
            }
        }

        // Restore correlation ID into baggage
        if (messageContext.Headers.TryGetValue(CorrelationIdHeader, out var corrId))
        {
            context.SetBaggage("correlation_id", corrId);
        }

        // Restore origin request ID — the consumer's causality chain starts here
        if (messageContext.Headers.TryGetValue(OriginRequestIdHeader, out var originId))
        {
            context.SetBaggage("origin_request_id", originId);
        }

        // Restore causality chain from the publishing process
        if (messageContext.Headers.TryGetValue(CausalityChainHeader, out var chainData))
        {
            var entries = ParseCausalityChain(chainData);
            foreach (var entry in entries)
            {
                context.RecordCausality(entry.RequestId, entry.ParentId, $"[remote] {entry.RequestType}");
            }
        }

        // Record the message consumption as a new causality entry
        context.RecordCausality(
            messageContext.MessageId.ToString("N")[..8],
            messageContext.Headers.TryGetValue(OriginRequestIdHeader, out var parentId) ? parentId : null,
            $"[consume] {messageContext.DestinationAddress ?? "unknown"}"
        );
    }

    /// <summary>
    /// Hydrates a pipeline context from raw headers dictionary (for InMemory transport).
    /// </summary>
    public static void HydrateContext(IPipelineContext context, IReadOnlyDictionary<string, string>? headers, IMessagingSigningKey? signingKey = null)
    {
        if (headers is null) return;

        var messageContext = new MessageContext
        {
            MessageId = Guid.NewGuid(),
            Headers = headers
        };

        HydrateContext(context, messageContext, signingKey);
    }

    private static void VerifySignatureOrThrow(
        IReadOnlyDictionary<string, string> headers,
        Guid messageId,
        IMessagingSigningKey? signingKey)
    {
        var signedSubset = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            if (!key.StartsWith(BaggagePrefix, StringComparison.Ordinal)) continue;
            var baggageKey = key[BaggagePrefix.Length..];
            if (SignedBaggageKeySet.Contains(baggageKey))
            {
                signedSubset[baggageKey] = value;
            }
        }

        if (signedSubset.Count == 0)
        {
            // No identity-bearing baggage in the message — nothing to verify.
            // Diagnostic-only baggage rides unsigned by design.
            return;
        }

        if (signingKey is null)
        {
            throw new IdentitySignatureMismatchException(
                $"Message {messageId:N} carries identity baggage but no IMessagingSigningKey is registered on the consumer. " +
                "Reason: identity-signature-invalid (no key).");
        }

        if (!headers.TryGetValue(SignatureHeader, out var providedSig) || string.IsNullOrEmpty(providedSig))
        {
            throw new IdentitySignatureMismatchException(
                $"Message {messageId:N} carries identity baggage without {SignatureHeader}. " +
                "Reason: identity-signature-invalid (missing).");
        }

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromBase64String(providedSig);
        }
        catch (FormatException)
        {
            throw new IdentitySignatureMismatchException(
                $"Message {messageId:N} has a malformed {SignatureHeader}. " +
                "Reason: identity-signature-invalid (decode).");
        }

        var expected = HmacSha256Raw(signedSubset, signingKey.GetKey());
        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expected))
        {
            throw new IdentitySignatureMismatchException(
                $"Message {messageId:N} identity signature did not verify. " +
                "Reason: identity-signature-invalid (mismatch).");
        }
    }

    private static string ComputeSignature(SortedDictionary<string, string> signedSubset, byte[] key)
    {
        var raw = HmacSha256Raw(signedSubset, key);
        return Convert.ToBase64String(raw);
    }

    private static byte[] HmacSha256Raw(SortedDictionary<string, string> signedSubset, byte[] key)
    {
        var canonical = CanonicalJson(signedSubset);
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>
    /// Emits the signed-subset values as deterministic JSON: keys sorted
    /// ASCII-bytewise (already by the SortedDictionary), no whitespace,
    /// Guid-shaped values normalised to lowercase 8-4-4-4-12,
    /// <c>group_ids</c> reshaped from CSV into a sorted JSON array so that
    /// permutations of the same group set produce the same signature.
    /// </summary>
    private static string CanonicalJson(SortedDictionary<string, string> signedSubset)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var (key, raw) in signedSubset)
            {
                if (string.Equals(key, "group_ids", StringComparison.Ordinal))
                {
                    writer.WritePropertyName(key);
                    writer.WriteStartArray();
                    foreach (var g in NormaliseGroupIds(raw))
                    {
                        writer.WriteStringValue(g);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString(key, NormaliseValue(raw));
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string NormaliseValue(string raw)
        => Guid.TryParse(raw, out var g) ? g.ToString("D") : raw;

    private static IEnumerable<string> NormaliseGroupIds(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return [];
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormaliseValue)
            .OrderBy(s => s, StringComparer.Ordinal);
    }

    private static List<CausalityEntry> ParseCausalityChain(string data)
    {
        var entries = new List<CausalityEntry>();

        foreach (var segment in data.Split('|'))
        {
            var parts = segment.Split(';');
            if (parts.Length < 4) continue;

            entries.Add(new CausalityEntry(
                parts[0],
                string.IsNullOrEmpty(parts[1]) ? null : parts[1],
                parts[2],
                DateTimeOffset.TryParse(parts[3], out var ts) ? ts : DateTimeOffset.UtcNow
            ));
        }

        return entries;
    }
}
