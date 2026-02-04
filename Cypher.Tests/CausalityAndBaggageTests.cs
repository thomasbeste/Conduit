using Microsoft.Extensions.DependencyInjection;

namespace Cypher.Tests;

public class CausalityAndBaggageTests
{
    #region Test Requests and Handlers

    public record SimpleRequest(string Value) : IRequest<string>;

    public class SimpleRequestHandler : IRequestHandler<SimpleRequest, string>
    {
        public Task<string> Handle(SimpleRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult($"Handled: {request.Value}");
        }
    }

    public record OuterRequest(string Value) : IRequest<string>;

    /// <summary>
    /// Handler that internally sends another request - for testing nested causality.
    /// </summary>
    public class OuterRequestHandler(ISender sender) : IRequestHandler<OuterRequest, string>
    {
        public async Task<string> Handle(OuterRequest request, CancellationToken cancellationToken)
        {
            var innerResult = await sender.Send(new InnerRequest(request.Value + "-inner"), cancellationToken);
            return $"Outer({innerResult})";
        }
    }

    public record InnerRequest(string Value) : IRequest<string>;

    public class InnerRequestHandler : IRequestHandler<InnerRequest, string>
    {
        public Task<string> Handle(InnerRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult($"Inner: {request.Value}");
        }
    }

    public record DeepNestedRequest(int Depth) : IRequest<int>;

    /// <summary>
    /// Handler that recursively sends requests to test deep nesting.
    /// </summary>
    public class DeepNestedRequestHandler(ISender sender) : IRequestHandler<DeepNestedRequest, int>
    {
        public async Task<int> Handle(DeepNestedRequest request, CancellationToken cancellationToken)
        {
            if (request.Depth <= 0)
                return 0;

            await sender.Send(new DeepNestedRequest(request.Depth - 1), cancellationToken);
            return request.Depth;
        }
    }

    /// <summary>
    /// Handler that reads baggage to verify it propagates.
    /// </summary>
    public record BaggageReadingRequest : IRequest<string?>;

    public class BaggageReadingRequestHandler(IEnumerable<IPipelineContext> contexts) : IRequestHandler<BaggageReadingRequest, string?>
    {
        public Task<string?> Handle(BaggageReadingRequest request, CancellationToken cancellationToken)
        {
            var ctx = contexts.FirstOrDefault();
            var tenantId = ctx?.GetBaggage("tenant_id");
            return Task.FromResult(tenantId);
        }
    }

    /// <summary>
    /// Handler that sends nested request and expects baggage to be available there too.
    /// </summary>
    public record NestedBaggageRequest : IRequest<string?>;

    public class NestedBaggageRequestHandler(ISender sender) : IRequestHandler<NestedBaggageRequest, string?>
    {
        public async Task<string?> Handle(NestedBaggageRequest request, CancellationToken cancellationToken)
        {
            return await sender.Send(new BaggageReadingRequest(), cancellationToken);
        }
    }

    #endregion

    #region Baggage Tests

    [Fact]
    public void Baggage_SetAndGet_Works()
    {
        var context = new PipelineContext();

        context.SetBaggage("tenant_id", "acme-corp");
        context.SetBaggage("user_id", "gilfoyle");

        Assert.Equal("acme-corp", context.GetBaggage("tenant_id"));
        Assert.Equal("gilfoyle", context.GetBaggage("user_id"));
    }

    [Fact]
    public void Baggage_GetNonExistent_ReturnsNull()
    {
        var context = new PipelineContext();

        Assert.Null(context.GetBaggage("does_not_exist"));
    }

    [Fact]
    public void Baggage_GetAllBaggage_ReturnsAll()
    {
        var context = new PipelineContext();

        context.SetBaggage("key1", "value1");
        context.SetBaggage("key2", "value2");

        var all = context.GetAllBaggage();

        Assert.Equal(2, all.Count);
        Assert.Equal("value1", all["key1"]);
        Assert.Equal("value2", all["key2"]);
    }

    [Fact]
    public void Baggage_Overwrite_Works()
    {
        var context = new PipelineContext();

        context.SetBaggage("key", "original");
        context.SetBaggage("key", "updated");

        Assert.Equal("updated", context.GetBaggage("key"));
    }

    [Fact]
    public async Task Baggage_AvailableInHandler()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        ctx.SetBaggage("tenant_id", "piedpiper");

        var result = await dispatcher.Send(new BaggageReadingRequest());

        Assert.Equal("piedpiper", result);
    }

    [Fact]
    public async Task Baggage_PropagatesAcrossNestedRequests()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        ctx.SetBaggage("tenant_id", "hooli");

        // This handler sends a nested request that reads baggage
        var result = await dispatcher.Send(new NestedBaggageRequest());

        Assert.Equal("hooli", result);
    }

    #endregion

    #region Causality Tests

    [Fact]
    public async Task Causality_SingleRequest_GeneratesRequestId()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnableCausalityTracking = true;
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new SimpleRequest("test"));

        var chain = ctx.GetCausalityChain();

        Assert.Single(chain);
        Assert.Equal("SimpleRequest", chain[0].RequestType);
        Assert.NotEmpty(chain[0].RequestId);
        Assert.Null(chain[0].ParentId); // Root request has no parent
    }

    [Fact]
    public async Task Causality_NestedRequests_TracksParentChild()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnableCausalityTracking = true;
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new OuterRequest("test"));

        var chain = ctx.GetCausalityChain();

        Assert.Equal(2, chain.Count);

        var outer = chain.First(c => c.RequestType == "OuterRequest");
        var inner = chain.First(c => c.RequestType == "InnerRequest");

        Assert.Null(outer.ParentId); // Root has no parent
        Assert.Equal(outer.RequestId, inner.ParentId); // Inner's parent is outer
    }

    [Fact]
    public async Task Causality_DeepNesting_TracksFullChain()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnableCausalityTracking = true;
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new DeepNestedRequest(3));

        var chain = ctx.GetCausalityChain();

        Assert.Equal(4, chain.Count); // Depth 3, 2, 1, 0

        // Verify parent-child relationships form a proper chain
        var root = chain.First(c => c.ParentId == null);
        Assert.NotNull(root);

        var current = root;
        var visited = new HashSet<string> { current.RequestId };

        while (true)
        {
            var child = chain.FirstOrDefault(c => c.ParentId == current.RequestId);
            if (child == null) break;

            Assert.DoesNotContain(child.RequestId, visited); // No cycles
            visited.Add(child.RequestId);
            current = child;
        }

        Assert.Equal(4, visited.Count);
    }

    [Fact]
    public async Task Causality_SequentialRequests_EachHasOwnId()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnableCausalityTracking = true;
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new SimpleRequest("first"));
        await dispatcher.Send(new SimpleRequest("second"));
        await dispatcher.Send(new SimpleRequest("third"));

        var chain = ctx.GetCausalityChain();

        Assert.Equal(3, chain.Count);

        // All are root requests (no parent relationship between sequential calls)
        Assert.All(chain, entry => Assert.Null(entry.ParentId));

        // All have unique IDs
        var ids = chain.Select(c => c.RequestId).ToHashSet();
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public async Task Causality_WithBaggageRequestId_UsesBaggageValue()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnableCausalityTracking = true;
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        // Simulate HTTP middleware setting request_id from X-Request-Id header
        ctx.SetBaggage("request_id", "http-trace-abc123");

        await dispatcher.Send(new SimpleRequest("test"));

        var chain = ctx.GetCausalityChain();

        Assert.Single(chain);
        Assert.Equal("http-trace-abc123", chain[0].RequestId);
    }

    [Fact]
    public async Task Causality_Disabled_NoTracking()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnableCausalityTracking = false; // Explicitly disabled
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new SimpleRequest("test"));

        var chain = ctx.GetCausalityChain();

        Assert.Empty(chain);
    }

    [Fact]
    public async Task Causality_DisabledContext_GracefullyNoOps()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnablePipelineContext = false;
            cfg.EnableCausalityTracking = true; // Won't matter without context
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        // Should not throw
        var result = await dispatcher.Send(new SimpleRequest("test"));

        Assert.Equal("Handled: test", result);
    }

    [Fact]
    public async Task Causality_ParallelRequests_ThreadSafe()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CausalityAndBaggageTests>();
            cfg.EnableCausalityTracking = true;
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ctx = scope.ServiceProvider.GetRequiredService<IPipelineContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        const int taskCount = 50;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(i => dispatcher.Send(new SimpleRequest($"parallel-{i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        var chain = ctx.GetCausalityChain();

        Assert.Equal(taskCount, chain.Count);

        // All should have unique IDs
        var ids = chain.Select(c => c.RequestId).ToHashSet();
        Assert.Equal(taskCount, ids.Count);
    }

    [Fact]
    public void GetCurrentRequestId_WithNoActiveRequest_ReturnsNull()
    {
        var context = new PipelineContext();

        Assert.Null(context.GetCurrentRequestId());
    }

    [Fact]
    public void GetParentRequestId_WithNoChain_ReturnsNull()
    {
        var context = new PipelineContext();

        Assert.Null(context.GetParentRequestId());
    }

    #endregion

    #region GetOrAdd Extension Tests

    [Fact]
    public void GetOrAdd_CreatesNewValueWhenMissing()
    {
        var items = new Dictionary<string, object?>();

        var result = items.GetOrAdd("key", () => new List<string> { "value" });

        Assert.Single(result);
        Assert.Equal("value", result[0]);
    }

    [Fact]
    public void GetOrAdd_ReturnsExistingValueWhenPresent()
    {
        var items = new Dictionary<string, object?>();
        var original = new List<string> { "original" };
        items["key"] = original;

        var result = items.GetOrAdd("key", () => new List<string> { "should not be used" });

        Assert.Same(original, result);
    }

    [Fact]
    public void GetOrAdd_ReplacesWrongType()
    {
        var items = new Dictionary<string, object?>();
        items["key"] = "a string, not a list";

        var result = items.GetOrAdd("key", () => new List<string> { "new list" });

        Assert.Single(result);
        Assert.Equal("new list", result[0]);
    }

    #endregion
}
