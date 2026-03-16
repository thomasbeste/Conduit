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
    /// Extracts pipeline context state (baggage, causality, correlation) into headers
    /// suitable for transport via message broker.
    /// </summary>
    public static Dictionary<string, string> ExtractHeaders(IPipelineContext context)
    {
        var headers = new Dictionary<string, string>();

        // Extract baggage
        var baggage = context.GetAllBaggage();
        foreach (var (key, value) in baggage)
        {
            headers[$"{BaggagePrefix}{key}"] = value;
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
    public static void HydrateContext(IPipelineContext context, MessageContext messageContext)
    {
        if (messageContext.Headers is null) return;

        // Restore baggage
        foreach (var (key, value) in messageContext.Headers)
        {
            if (key.StartsWith(BaggagePrefix, StringComparison.Ordinal) && value is string strValue)
            {
                var baggageKey = key[BaggagePrefix.Length..];
                context.SetBaggage(baggageKey, strValue);
            }
        }

        // Restore correlation ID into baggage
        if (messageContext.Headers.TryGetValue(CorrelationIdHeader, out var corrId) && corrId is string corrIdStr)
        {
            context.SetBaggage("correlation_id", corrIdStr);
        }

        // Restore origin request ID — the consumer's causality chain starts here
        if (messageContext.Headers.TryGetValue(OriginRequestIdHeader, out var originId) && originId is string originIdStr)
        {
            context.SetBaggage("origin_request_id", originIdStr);
        }

        // Restore causality chain from the publishing process
        if (messageContext.Headers.TryGetValue(CausalityChainHeader, out var chainStr) && chainStr is string chainData)
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
            messageContext.Headers.TryGetValue(OriginRequestIdHeader, out var parentId) ? parentId?.ToString() : null,
            $"[consume] {messageContext.DestinationAddress ?? "unknown"}"
        );
    }

    /// <summary>
    /// Hydrates a pipeline context from raw headers dictionary (for InMemory transport).
    /// </summary>
    public static void HydrateContext(IPipelineContext context, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;

        // Create a minimal MessageContext wrapper for reuse
        var messageContext = new MessageContext
        {
            MessageId = Guid.NewGuid(),
            Headers = headers.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
        };

        HydrateContext(context, messageContext);
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
