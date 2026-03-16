namespace Conduit;

/// <summary>
/// Extension methods for ergonomic access to cross-request context patterns.
/// </summary>
public static class PipelineContextExtensions
{
    /// <summary>
    /// Gets an existing value or adds a new one using the factory.
    /// </summary>
    public static T GetOrAdd<T>(this IDictionary<string, object?> items, string key, Func<T> factory)
    {
        if (items.TryGetValue(key, out var existing) && existing is T typed)
            return typed;

        var value = factory();
        items[key] = value;
        return value;
    }

    #region Baggage

    /// <summary>
    /// Sets a baggage value that flows through all requests in the current scope.
    /// </summary>
    public static void SetBaggage(this IPipelineContext ctx, string key, string value)
    {
        var baggage = ctx.Items.GetOrAdd(ContextKeys.Baggage, () => new Dictionary<string, string>());
        baggage[key] = value;
    }

    /// <summary>
    /// Gets a baggage value from the current scope.
    /// </summary>
    public static string? GetBaggage(this IPipelineContext ctx, string key)
    {
        if (!ctx.Items.TryGetValue(ContextKeys.Baggage, out var b) || b is not Dictionary<string, string> baggage)
            return null;

        return baggage.GetValueOrDefault(key);
    }

    /// <summary>
    /// Gets all baggage values from the current scope.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetAllBaggage(this IPipelineContext ctx)
    {
        if (!ctx.Items.TryGetValue(ContextKeys.Baggage, out var b) || b is not Dictionary<string, string> baggage)
            return new Dictionary<string, string>();

        return baggage;
    }

    #endregion

    #region Causality

    /// <summary>
    /// Gets the current request ID within the causality chain.
    /// </summary>
    public static string? GetCurrentRequestId(this IPipelineContext ctx)
    {
        return ctx.Items.TryGetValue(ContextKeys.CurrentRequestId, out var id) ? (string?)id : null;
    }

    /// <summary>
    /// Gets the parent request ID (the request that spawned the current one).
    /// Returns null if this is a root request.
    /// </summary>
    public static string? GetParentRequestId(this IPipelineContext ctx)
    {
        var chain = ctx.GetCausalityChain();
        var currentId = ctx.GetCurrentRequestId();

        if (currentId is null || chain.Count == 0)
            return null;

        var current = chain.FirstOrDefault(e => e.RequestId == currentId);
        return current?.ParentId;
    }

    /// <summary>
    /// Gets the full causality chain of all requests in this scope.
    /// </summary>
    public static IReadOnlyList<CausalityEntry> GetCausalityChain(this IPipelineContext ctx)
    {
        if (!ctx.Items.TryGetValue(ContextKeys.CausalityChain, out var c) || c is not List<CausalityEntry> chain)
            return [];

        return chain;
    }

    /// <summary>
    /// Records a causality entry. Used internally by CausalityBehavior.
    /// </summary>
    internal static void RecordCausality(this IPipelineContext ctx, string requestId, string? parentId, string requestType)
    {
        var chain = ctx.Items.GetOrAdd(ContextKeys.CausalityChain, () => new List<CausalityEntry>());
        chain.Add(new CausalityEntry(requestId, parentId, requestType, DateTimeOffset.UtcNow));
    }

    #endregion
}
