using System.Text;
using Conduit.Mediator;
using Conduit.Messaging;
using Conduit.Messaging.Bridge;

namespace Conduit.Tests;

/// <summary>
/// Covers #897: identity-bearing baggage must HMAC-verify on hydrate;
/// missing/tampered/wrong-key signatures fail loud with
/// <see cref="IdentitySignatureMismatchException"/> so the consumer
/// host DLQs rather than dispatching forged identity.
/// </summary>
public class PipelineContextBridgeSignatureTests
{
    private sealed class FixedKey(byte[] key) : IMessagingSigningKey
    {
        public byte[] GetKey() => key;
    }

    private static IMessagingSigningKey KeyA => new FixedKey(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
    private static IMessagingSigningKey KeyB => new FixedKey(Encoding.UTF8.GetBytes("ffffffffffffffffffffffffffffffff"));

    private static PipelineContext NewIdentityCtx()
    {
        var ctx = new PipelineContext();
        ctx.SetBaggage("tenant_id", Guid.Parse("11111111-1111-1111-1111-111111111111").ToString());
        ctx.SetBaggage("user_id", Guid.Parse("22222222-2222-2222-2222-222222222222").ToString());
        ctx.SetBaggage("user_role", "User");
        ctx.SetBaggage("group_ids", "33333333-3333-3333-3333-333333333333,44444444-4444-4444-4444-444444444444");
        ctx.SetBaggage("session_id", Guid.Parse("55555555-5555-5555-5555-555555555555").ToString());
        return ctx;
    }

    [Fact]
    public void Roundtrip_Preserves_All_Signed_Identity_Keys()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);

        Assert.True(headers.ContainsKey(PipelineContextBridge.SignatureHeader));

        var target = new PipelineContext();
        PipelineContextBridge.HydrateContext(target, headers, KeyA);

        Assert.Equal("11111111-1111-1111-1111-111111111111", target.GetBaggage("tenant_id"));
        Assert.Equal("22222222-2222-2222-2222-222222222222", target.GetBaggage("user_id"));
        Assert.Equal("User", target.GetBaggage("user_role"));
        Assert.Equal("55555555-5555-5555-5555-555555555555", target.GetBaggage("session_id"));
        Assert.Equal(
            "33333333-3333-3333-3333-333333333333,44444444-4444-4444-4444-444444444444",
            target.GetBaggage("group_ids"));
    }

    [Fact]
    public void Tampered_Tenant_Fails_Verification()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);

        // Adversary swaps tenant_id for one they don't belong to.
        headers["conduit.baggage.tenant_id"] = Guid.NewGuid().ToString();

        var target = new PipelineContext();
        Assert.Throws<IdentitySignatureMismatchException>(() =>
            PipelineContextBridge.HydrateContext(target, headers, KeyA));
    }

    [Fact]
    public void Tampered_Role_Fails_Verification()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);

        // Privilege-escalation attempt.
        headers["conduit.baggage.user_role"] = "SystemAdmin";

        var target = new PipelineContext();
        Assert.Throws<IdentitySignatureMismatchException>(() =>
            PipelineContextBridge.HydrateContext(target, headers, KeyA));
    }

    [Fact]
    public void Missing_Signature_Header_Fails_Verification()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);

        // Adversary strips the signature hoping the consumer treats absence as "legacy unsigned".
        headers.Remove(PipelineContextBridge.SignatureHeader);

        var target = new PipelineContext();
        Assert.Throws<IdentitySignatureMismatchException>(() =>
            PipelineContextBridge.HydrateContext(target, headers, KeyA));
    }

    [Fact]
    public void Wrong_Key_Fails_Verification()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);

        var target = new PipelineContext();
        Assert.Throws<IdentitySignatureMismatchException>(() =>
            PipelineContextBridge.HydrateContext(target, headers, KeyB));
    }

    [Fact]
    public void Missing_Consumer_Key_Fails_When_Identity_Present()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);

        var target = new PipelineContext();
        Assert.Throws<IdentitySignatureMismatchException>(() =>
            PipelineContextBridge.HydrateContext(target, headers, signingKey: null));
    }

    [Fact]
    public void Group_Id_Order_Does_Not_Change_Signature()
    {
        var a = new PipelineContext();
        a.SetBaggage("tenant_id", Guid.Parse("11111111-1111-1111-1111-111111111111").ToString());
        a.SetBaggage("user_id", Guid.Parse("22222222-2222-2222-2222-222222222222").ToString());
        a.SetBaggage("user_role", "User");
        a.SetBaggage("session_id", Guid.Parse("55555555-5555-5555-5555-555555555555").ToString());
        a.SetBaggage("group_ids", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa,bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var b = new PipelineContext();
        b.SetBaggage("tenant_id", Guid.Parse("11111111-1111-1111-1111-111111111111").ToString());
        b.SetBaggage("user_id", Guid.Parse("22222222-2222-2222-2222-222222222222").ToString());
        b.SetBaggage("user_role", "User");
        b.SetBaggage("session_id", Guid.Parse("55555555-5555-5555-5555-555555555555").ToString());
        b.SetBaggage("group_ids", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb,aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var sigA = PipelineContextBridge.ExtractHeaders(a, KeyA)[PipelineContextBridge.SignatureHeader];
        var sigB = PipelineContextBridge.ExtractHeaders(b, KeyA)[PipelineContextBridge.SignatureHeader];

        Assert.Equal(sigA, sigB);
    }

    [Fact]
    public void Guid_Casing_Does_Not_Change_Signature()
    {
        var lower = new PipelineContext();
        lower.SetBaggage("tenant_id", "11111111-1111-1111-1111-111111111111");
        lower.SetBaggage("user_id", "22222222-2222-2222-2222-222222222222");
        lower.SetBaggage("user_role", "User");
        lower.SetBaggage("session_id", "55555555-5555-5555-5555-555555555555");

        var upper = new PipelineContext();
        upper.SetBaggage("tenant_id", "11111111-1111-1111-1111-111111111111".ToUpperInvariant());
        upper.SetBaggage("user_id", "22222222-2222-2222-2222-222222222222".ToUpperInvariant());
        upper.SetBaggage("user_role", "User");
        upper.SetBaggage("session_id", "55555555-5555-5555-5555-555555555555".ToUpperInvariant());

        var sigLower = PipelineContextBridge.ExtractHeaders(lower, KeyA)[PipelineContextBridge.SignatureHeader];
        var sigUpper = PipelineContextBridge.ExtractHeaders(upper, KeyA)[PipelineContextBridge.SignatureHeader];

        Assert.Equal(sigLower, sigUpper);
    }

    [Fact]
    public void Unsigned_Baggage_Survives_Roundtrip_And_Is_Not_In_Signature()
    {
        var source = NewIdentityCtx();
        source.SetBaggage("correlation_id", "corr-xyz");
        source.SetBaggage("feature_flags", "beta,dark-mode");

        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);
        var originalSig = headers[PipelineContextBridge.SignatureHeader];

        // Tamper with unsigned baggage — sig must NOT cover it.
        headers["conduit.baggage.feature_flags"] = "ga,light-mode";

        var target = new PipelineContext();
        PipelineContextBridge.HydrateContext(target, headers, KeyA);

        Assert.Equal("corr-xyz", target.GetBaggage("correlation_id"));
        Assert.Equal("ga,light-mode", target.GetBaggage("feature_flags"));

        // Re-extracting the original (untampered) baggage with the same key
        // must yield the same signature — proves correlation/feature flags
        // were never part of the signed payload.
        var resignedHeaders = PipelineContextBridge.ExtractHeaders(source, KeyA);
        Assert.Equal(originalSig, resignedHeaders[PipelineContextBridge.SignatureHeader]);
    }

    [Fact]
    public void No_Identity_Baggage_Yields_No_Signature_And_Hydrates_Without_Key()
    {
        var source = new PipelineContext();
        source.SetBaggage("correlation_id", "corr-123");
        source.SetBaggage("feature_flags", "beta");

        var headers = PipelineContextBridge.ExtractHeaders(source);

        Assert.False(headers.ContainsKey(PipelineContextBridge.SignatureHeader));

        var target = new PipelineContext();
        PipelineContextBridge.HydrateContext(target, headers);

        Assert.Equal("corr-123", target.GetBaggage("correlation_id"));
    }

    [Fact]
    public void Extract_Without_Key_When_Identity_Present_Throws()
    {
        var source = NewIdentityCtx();

        Assert.Throws<InvalidOperationException>(() =>
            PipelineContextBridge.ExtractHeaders(source));
    }

    [Fact]
    public void Hydrate_From_MessageContext_Verifies_Signature()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);

        var messageContext = new MessageContext
        {
            MessageId = Guid.NewGuid(),
            Headers = headers
        };

        var target = new PipelineContext();
        PipelineContextBridge.HydrateContext(target, messageContext, KeyA);

        Assert.Equal("User", target.GetBaggage("user_role"));
    }

    [Fact]
    public void Malformed_Base64_Signature_Fails_Verification()
    {
        var source = NewIdentityCtx();
        var headers = PipelineContextBridge.ExtractHeaders(source, KeyA);
        headers[PipelineContextBridge.SignatureHeader] = "not-base64-!@#";

        var target = new PipelineContext();
        Assert.Throws<IdentitySignatureMismatchException>(() =>
            PipelineContextBridge.HydrateContext(target, headers, KeyA));
    }
}
