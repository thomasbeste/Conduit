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
        private readonly HashSet<string> _releasedConsumers = new(StringComparer.Ordinal);
        public int ReleaseCount { get; private set; }
        public Guid? DeletedId { get; private set; }
        public Exception? DeleteFailure { get; set; }
        public int ReleaseFailuresRemaining { get; set; }
        public int ExpectedConsumers { get; private set; }

        public Task<Guid> StoreAsync(
            ReadOnlyMemory<byte> payload,
            string contentType,
            int expectedConsumers,
            CancellationToken cancellationToken = default)
        {
            StoreCount++;
            var id = Guid.NewGuid();
            _blobs[id] = payload.ToArray();
            ExpectedConsumers = expectedConsumers;
            return Task.FromResult(id);
        }

        public Task<byte[]?> GetAsync(Guid payloadId, CancellationToken cancellationToken = default)
        {
            GetCount++;
            _blobs.TryGetValue(payloadId, out var blob);
            return Task.FromResult(blob);
        }

        public Task ReleaseAsync(
            Guid payloadId,
            string consumerId,
            CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            DeletedId = payloadId;
            if (DeleteFailure is not null && ReleaseFailuresRemaining > 0)
            {
                ReleaseFailuresRemaining--;
                return Task.FromException(DeleteFailure);
            }
            _releasedConsumers.Add(consumerId);
            if (_releasedConsumers.Count >= ExpectedConsumers)
                _blobs.TryRemove(payloadId, out _);
            return Task.CompletedTask;
        }

        /// <summary>Simulate a missing blob: drop it but keep the reference live.</summary>
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

        // The blob disappears while the message still carries the reference.
        store.Evict(reference);

        var ex = await Assert.ThrowsAsync<ClaimCheckMissingException>(() =>
            ClaimCheck.RehydrateAsync(offload.Body, HeaderFrom(reference), store, Ct));

        Assert.Equal(reference, ex.PayloadId);
    }

    [Fact]
    public async Task SettleAndRelease_Releases_Only_After_Settlement_Succeeds()
    {
        var store = new FakeStore();
        var offload = await ClaimCheck.OffloadAsync(BigBody(), "application/json", store, cancellationToken: Ct);
        var settled = false;

        await ClaimCheck.SettleAndReleaseAsync(
            offload.Reference,
            store,
            "service-a-handler",
            _ =>
            {
                Assert.Equal(0, store.ReleaseCount);
                settled = true;
                return Task.CompletedTask;
            },
            Ct);

        Assert.True(settled);
        Assert.Equal(1, store.ReleaseCount);
        Assert.Equal(offload.Reference, store.DeletedId);
    }

    [Fact]
    public async Task SettleAndRelease_Settlement_Failure_Preserves_Payload()
    {
        var store = new FakeStore();
        var offload = await ClaimCheck.OffloadAsync(BigBody(), "application/json", store, cancellationToken: Ct);
        var settlementFailure = new InvalidOperationException("broker unavailable");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClaimCheck.SettleAndReleaseAsync(
                offload.Reference,
                store,
                "service-a-handler",
                _ => Task.FromException(settlementFailure),
                Ct));

        Assert.Same(settlementFailure, thrown);
        Assert.Equal(0, store.ReleaseCount);
        Assert.NotNull(await store.GetAsync(offload.Reference!.Value, Ct));
    }

    [Fact]
    public async Task SettleAndRelease_Cleanup_Failure_Is_Classified_As_Post_Settlement()
    {
        var cleanupFailure = new InvalidOperationException("database unavailable");
        var store = new FakeStore
        {
            DeleteFailure = cleanupFailure,
            ReleaseFailuresRemaining = int.MaxValue
        };
        var offload = await ClaimCheck.OffloadAsync(BigBody(), "application/json", store, cancellationToken: Ct);
        var settled = false;

        var thrown = await Assert.ThrowsAsync<ClaimCheckCleanupException>(() =>
            ClaimCheck.SettleAndReleaseAsync(
                offload.Reference,
                store,
                "service-a-handler",
                _ =>
                {
                    settled = true;
                    return Task.CompletedTask;
                },
                Ct));

        Assert.True(settled);
        Assert.Equal(offload.Reference, thrown.PayloadId);
        Assert.Same(cleanupFailure, thrown.InnerException);
        Assert.Equal(3, store.ReleaseCount);
    }

    [Fact]
    public async Task SettleAndRelease_Retries_Transient_PostSettlement_Cleanup()
    {
        var store = new FakeStore
        {
            DeleteFailure = new InvalidOperationException("database temporarily unavailable"),
            ReleaseFailuresRemaining = 2
        };
        var offload = await ClaimCheck.OffloadAsync(BigBody(), "application/json", store, cancellationToken: Ct);
        var settled = false;

        await ClaimCheck.SettleAndReleaseAsync(
            offload.Reference,
            store,
            "service-a-handler",
            _ =>
            {
                settled = true;
                return Task.CompletedTask;
            },
            Ct);

        Assert.True(settled);
        Assert.Equal(3, store.ReleaseCount);
        Assert.Null(await store.GetAsync(offload.Reference!.Value, Ct));
    }

    [Fact]
    public async Task Shared_Payload_Remains_Until_Every_Distinct_Consumer_Settles()
    {
        var store = new FakeStore();
        var offload = await ClaimCheck.OffloadAsync(
            BigBody(),
            "application/json",
            store,
            expectedConsumers: 2,
            cancellationToken: Ct);
        var reference = offload.Reference!.Value;
        Assert.Equal(2, store.ExpectedConsumers);

        await ClaimCheck.SettleAndReleaseAsync(
            reference, store, "service-indexing-worker-handler", _ => Task.CompletedTask, Ct);
        Assert.NotNull(await store.GetAsync(reference, Ct));

        // A duplicate redelivery from the first queue is idempotent and cannot
        // consume the second queue's ownership slot.
        await ClaimCheck.SettleAndReleaseAsync(
            reference, store, "service-indexing-worker-handler", _ => Task.CompletedTask, Ct);
        Assert.NotNull(await store.GetAsync(reference, Ct));

        await ClaimCheck.SettleAndReleaseAsync(
            reference, store, "service-datahub-handler", _ => Task.CompletedTask, Ct);
        Assert.Null(await store.GetAsync(reference, Ct));
    }
}
