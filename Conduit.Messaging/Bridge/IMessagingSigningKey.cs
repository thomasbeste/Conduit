namespace Conduit.Messaging.Bridge;

/// <summary>
/// Source of the HMAC-SHA256 key used to sign the identity-bearing
/// subset of Conduit baggage on publish and verify it on consume.
///
/// Implementations should derive a 32-byte key once (HKDF from
/// installation master key, KMS-wrapped material, etc.) and cache it —
/// every publish and every consume calls <see cref="GetKey"/>, so any
/// per-call I/O would be hot-path overhead.
/// </summary>
public interface IMessagingSigningKey
{
    /// <summary>
    /// Returns the 32-byte HMAC-SHA256 signing key. Must be deterministic
    /// across the deployment — every publisher and consumer that share a
    /// bus must derive the same bytes or signature verification fails.
    /// </summary>
    byte[] GetKey();
}
