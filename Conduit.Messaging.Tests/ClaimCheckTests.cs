using System.Collections.Concurrent;
using System.Text;

namespace Conduit.Messaging.Tests;

/// <summary>
/// Unit tests for the transport-level claim-check codec (#2432). Exercises the
/// three invariants every transport relies on: (1) a body over the threshold is
/// offloaded to the store and replaced by a placeholder + reference; (2) a body
/// at/under the threshold — or any body with no store — rides inline untouched;
/// (3) a reference whose blob is gone throws <see cref="ClaimCheckMissingException"/>
/// so the consumer dead-letters rather than retries.
/// </summary>
public class ClaimCheckTests
{
    /// <summary>In-memory <see cref="IClaimCheckStore"/> that records call counts.</summary>
    private sealed class FakeStore : IClaimCheckStore
    {
        private readonly ConcurrentDictionary<Guid, byte[]> _blobs = new();
        public int StoreCount { get; private set; }
        public int GetCount { get; private set; }

        public Task<Guid> StoreAsync(ReadOnlyMemory<byte> payload, string contentType, CancellationToken cancellationToken = default)
        {
            StoreCount++;
            var id = Guid.NewGuid();
            _blobs[id] = payload.ToArray();
            return Task.FromResult(id);
        }

        public Task<byte[]?> GetAsync(Guid payloadId, CancellationToken cancellationToken = default)
        {
            GetCount++;
            _blobs.TryGetValue(payloadId, out var blob);
            return Task.FromResult(blob);
        }

        /// <summary>Simulate a TTL reap: drop the blob but keep the reference live.</summary>
        public void Evict(Guid id) => _blobs.TryRemove(id, out _);
    }

    private static byte[] BigBody() => new byte[ClaimCheck.DefaultThresholdBytes + 1];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<string, string?> HeaderFrom(Guid? reference) =>
        key => key == ClaimCheck.HeaderKey && reference is Guid r ? r.ToString("D") : null;

    [Fact]
    public async Task Offload_Then_Rehydrate_Roundtrips_Large_Body()
    {
        var store = new FakeStore();
        var original = Encoding.UTF8.GetBytes(new string('x', ClaimCheck.DefaultThresholdBytes + 1));

        var offload = await ClaimCheck.OffloadAsync(original, "application/json", store, cancellationToken: Ct);

        // Body was replaced by a small placeholder + a reference emitted.
        Assert.NotNull(offload.Reference);
        Assert.True(offload.Body.Length < original.Length);
        Assert.Equal(1, store.StoreCount);

        var rehydrated = await ClaimCheck.RehydrateAsync(
            offload.Body, HeaderFrom(offload.Reference), store, Ct);

        Assert.Equal(original, rehydrated.ToArray());
        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public async Task Small_Body_Is_Not_Offloaded()
    {
        var store = new FakeStore();
        var small = Encoding.UTF8.GetBytes("{\"hi\":true}");

        var offload = await ClaimCheck.OffloadAsync(small, "application/json", store, cancellationToken: Ct);

        Assert.Null(offload.Reference);
        Assert.Equal(small, offload.Body.ToArray());
        Assert.Equal(0, store.StoreCount);
    }

    [Fact]
    public async Task No_Store_Is_A_Noop_Even_For_Large_Body()
    {
        var big = BigBody();

        var offload = await ClaimCheck.OffloadAsync(big, "application/json", store: null, cancellationToken: Ct);

        Assert.Null(offload.Reference);
        Assert.Equal(big.Length, offload.Body.Length);

        // Rehydrate with no header is likewise a straight pass-through.
        var rehydrated = await ClaimCheck.RehydrateAsync(
            offload.Body, HeaderFrom(null), store: null, Ct);
        Assert.Equal(big.Length, rehydrated.Length);
    }

    [Fact]
    public async Task Rehydrate_Without_Reference_Returns_Body_Unchanged()
    {
        var store = new FakeStore();
        var body = Encoding.UTF8.GetBytes("inline");

        var rehydrated = await ClaimCheck.RehydrateAsync(body, HeaderFrom(null), store, Ct);

        Assert.Equal(body, rehydrated.ToArray());
        Assert.Equal(0, store.GetCount);
    }

    [Fact]
    public async Task Rehydrate_Missing_Blob_Throws_ClaimCheckMissing()
    {
        var store = new FakeStore();
        var offload = await ClaimCheck.OffloadAsync(BigBody(), "application/json", store, cancellationToken: Ct);
        var reference = offload.Reference!.Value;

        // TTL reaper drops the blob while the message still carries the reference.
        store.Evict(reference);

        var ex = await Assert.ThrowsAsync<ClaimCheckMissingException>(() =>
            ClaimCheck.RehydrateAsync(offload.Body, HeaderFrom(reference), store, Ct));

        Assert.Equal(reference, ex.PayloadId);
    }
}
