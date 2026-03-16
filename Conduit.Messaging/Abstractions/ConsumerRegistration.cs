namespace Conduit.Messaging;

/// <summary>
/// Registration of a consumer type and its message type.
/// </summary>
public sealed class ConsumerRegistration
{
    public required Type ConsumerType { get; init; }
    public required Type MessageType { get; init; }
}
