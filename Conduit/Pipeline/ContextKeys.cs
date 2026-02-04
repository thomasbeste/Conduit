namespace Conduit;

/// <summary>
/// Internal keys for well-known context items.
/// </summary>
internal static class ContextKeys
{
    public const string CurrentRequestId = "Conduit.CurrentRequestId";
    public const string CausalityChain = "Conduit.CausalityChain";
    public const string Baggage = "Conduit.Baggage";
}
