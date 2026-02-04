namespace Cypher;

/// <summary>
/// Pipeline behavior that automatically tracks request causality chains.
/// Tracks which request spawned which, enabling debugging and distributed tracing.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <remarks>
/// <para>
/// This behavior is automatically registered when <see cref="CypherConfiguration.EnableCausalityTracking"/> is set to <c>true</c>.
/// </para>
///
/// <para><b>Request ID resolution:</b></para>
/// <list type="number">
///   <item>Checks for "request_id" in baggage (set by HTTP middleware via <c>ctx.SetBaggage("request_id", id)</c>)</item>
///   <item>Falls back to generating a short random ID if not present</item>
/// </list>
///
/// <para><b>Example - Setting request ID from HTTP middleware:</b></para>
/// <code>
/// app.Use(async (httpContext, next) =>
/// {
///     var ctx = httpContext.RequestServices.GetService&lt;IPipelineContext&gt;();
///     var requestId = httpContext.Request.Headers["X-Request-Id"].FirstOrDefault()
///                     ?? httpContext.TraceIdentifier;
///     ctx?.SetBaggage("request_id", requestId);
///     await next();
/// });
/// </code>
///
/// <para><b>Accessing the causality chain:</b></para>
/// <code>
/// var chain = ctx.GetCausalityChain();
/// foreach (var entry in chain)
/// {
///     Console.WriteLine($"{entry.RequestType}: {entry.RequestId} (parent: {entry.ParentId})");
/// }
/// </code>
/// </remarks>
public sealed class CausalityBehavior<TRequest, TResponse>(IEnumerable<IPipelineContext> contexts)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var ctx = contexts.FirstOrDefault();
        if (ctx is null)
            return await next();

        // Get existing request ID from baggage (set by HTTP pipeline) or generate fallback
        var requestId = ctx.GetBaggage("request_id") ?? Guid.NewGuid().ToString("N")[..8];
        var parentId = ctx.Items.TryGetValue(ContextKeys.CurrentRequestId, out var p) ? (string?)p : null;

        ctx.RecordCausality(requestId, parentId, typeof(TRequest).Name);

        ctx.Items[ContextKeys.CurrentRequestId] = requestId;
        try
        {
            return await next();
        }
        finally
        {
            ctx.Items[ContextKeys.CurrentRequestId] = parentId;
        }
    }
}
