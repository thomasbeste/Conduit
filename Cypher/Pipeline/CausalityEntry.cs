namespace Cypher;

/// <summary>
/// Represents an entry in the causality chain, tracking request parent-child relationships.
/// </summary>
public record CausalityEntry(
    string RequestId,
    string? ParentId,
    string RequestType,
    DateTimeOffset Timestamp
);
