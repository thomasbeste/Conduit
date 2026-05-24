namespace Conduit.Messaging.Bridge;

/// <summary>
/// Thrown by <see cref="PipelineContextBridge.HydrateContext(Conduit.Mediator.IPipelineContext, MessageContext, IMessagingSigningKey?)"/>
/// when the incoming message's identity-bearing baggage cannot be verified
/// against the expected HMAC. Triggers DLQ on the consumer host rather
/// than letting forged identity flow into downstream authorisation
/// checks. See #897.
/// </summary>
public sealed class IdentitySignatureMismatchException : Exception
{
    public IdentitySignatureMismatchException(string message) : base(message) { }
}
