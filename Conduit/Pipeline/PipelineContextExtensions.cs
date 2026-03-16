namespace Conduit.Mediator;

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

    extension(IPipelineContext ctx)
    {
        /// <summary>
        /// Sets a baggage value that flows through all requests in the current scope.
        /// </summary>
        public void SetBaggage(string key, string value)
        {
            var baggage = ctx.Items.GetOrAdd(ContextKeys.Baggage, () => new Dictionary<string, string>());
            baggage[key] = value;
        }

        /// <summary>
        /// Gets a baggage value from the current scope.
        /// </summary>
        public string? GetBaggage(string key)
        {
            if (!ctx.Items.TryGetValue(ContextKeys.Baggage, out var b) || b is not Dictionary<string, string> baggage)
                return null;

            return baggage.GetValueOrDefault(key);
        }

        /// <summary>
        /// Gets all baggage values from the current scope.
        /// </summary>
        public IReadOnlyDictionary<string, string> GetAllBaggage()
        {
            if (!ctx.Items.TryGetValue(ContextKeys.Baggage, out var b) || b is not Dictionary<string, string> baggage)
                return new Dictionary<string, string>();

            return baggage;
        }
    }

    #endregion

    #region Causality

    extension(IPipelineContext ctx)
    {
        /// <summary>
        /// Gets the current request ID within the causality chain.
        /// </summary>
        public string? GetCurrentRequestId()
        {
            return ctx.Items.TryGetValue(ContextKeys.CurrentRequestId, out var id) ? (string?)id : null;
        }

        /// <summary>
        /// Gets the parent request ID (the request that spawned the current one).
        /// Returns null if this is a root request.
        /// </summary>
        public string? GetParentRequestId()
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
        public IReadOnlyList<CausalityEntry> GetCausalityChain()
        {
            if (!ctx.Items.TryGetValue(ContextKeys.CausalityChain, out var c) || c is not List<CausalityEntry> chain)
                return [];

            return chain;
        }

        /// <summary>
        /// Records a causality entry. Used by CausalityBehavior and cross-process bridge.
        /// </summary>
        public void RecordCausality(string requestId, string? parentId, string requestType)
        {
            var chain = ctx.Items.GetOrAdd(ContextKeys.CausalityChain, () => new List<CausalityEntry>());
            chain.Add(new CausalityEntry(requestId, parentId, requestType, DateTimeOffset.UtcNow));
        }
    }

    #endregion
}
