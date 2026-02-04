namespace Cypher;

/// <summary>
/// Internal keys for well-known context items.
/// </summary>
internal static class ContextKeys
{
    public const string CurrentRequestId = "Cypher.CurrentRequestId";
    public const string CausalityChain = "Cypher.CausalityChain";
    public const string Baggage = "Cypher.Baggage";
}
