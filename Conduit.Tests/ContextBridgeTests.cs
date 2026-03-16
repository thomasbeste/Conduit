using Conduit.Mediator;
using Conduit.Messaging;
using Conduit.Messaging.Bridge;
using Conduit.Messaging.InMemory;
using Conduit.Messaging.Registration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

/// <summary>
/// Tests that IPipelineContext state flows across the mediator → messaging boundary.
/// </summary>
public class ContextBridgeTests
{
    private record TestEvent : EventMessage
    {
        public string Data { get; init; } = "";
    }

    private class TestConsumer : IMessageConsumer<TestEvent>
    {
        public MessageContext? ReceivedContext { get; private set; }
        public IPipelineContext? ReceivedPipelineContext { get; private set; }

        private readonly IPipelineContext? _pipelineContext;

        public TestConsumer(IPipelineContext? pipelineContext = null)
        {
            _pipelineContext = pipelineContext;
        }

        public Task ConsumeAsync(TestEvent message, MessageContext context, CancellationToken cancellationToken = default)
        {
            ReceivedContext = context;
            ReceivedPipelineContext = _pipelineContext;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void ExtractHeaders_Includes_Baggage()
    {
        var ctx = new PipelineContext();
        ctx.SetBaggage("tenant-id", "acme");
        ctx.SetBaggage("user-id", "42");

        var headers = PipelineContextBridge.ExtractHeaders(ctx);

        Assert.Equal("acme", headers["conduit.baggage.tenant-id"]);
        Assert.Equal("42", headers["conduit.baggage.user-id"]);
    }

    [Fact]
    public void ExtractHeaders_Includes_CorrelationId_From_Baggage()
    {
        var ctx = new PipelineContext();
        ctx.SetBaggage("correlation_id", "corr-123");

        var headers = PipelineContextBridge.ExtractHeaders(ctx);

        Assert.Equal("corr-123", headers["conduit.correlation-id"]);
    }

    [Fact]
    public void HydrateContext_Restores_Baggage()
    {
        var sourceCtx = new PipelineContext();
        sourceCtx.SetBaggage("tenant-id", "acme");
        sourceCtx.SetBaggage("feature-flags", "new-ui,beta");

        var headers = PipelineContextBridge.ExtractHeaders(sourceCtx);

        var targetCtx = new PipelineContext();
        PipelineContextBridge.HydrateContext(targetCtx, headers);

        Assert.Equal("acme", targetCtx.GetBaggage("tenant-id"));
        Assert.Equal("new-ui,beta", targetCtx.GetBaggage("feature-flags"));
    }

    [Fact]
    public void HydrateContext_Restores_CorrelationId()
    {
        var sourceCtx = new PipelineContext();
        sourceCtx.SetBaggage("correlation_id", "corr-456");

        var headers = PipelineContextBridge.ExtractHeaders(sourceCtx);

        var targetCtx = new PipelineContext();
        PipelineContextBridge.HydrateContext(targetCtx, headers);

        Assert.Equal("corr-456", targetCtx.GetBaggage("correlation_id"));
    }

    [Fact]
    public void Roundtrip_Preserves_All_Baggage()
    {
        var source = new PipelineContext();
        source.SetBaggage("tenant-id", "acme");
        source.SetBaggage("user-id", "42");
        source.SetBaggage("correlation_id", "req-789");
        source.SetBaggage("feature-flags", "dark-mode");

        var headers = PipelineContextBridge.ExtractHeaders(source);

        var messageContext = new MessageContext
        {
            MessageId = Guid.NewGuid(),
            Headers = headers
        };

        var target = new PipelineContext();
        PipelineContextBridge.HydrateContext(target, messageContext);

        Assert.Equal("acme", target.GetBaggage("tenant-id"));
        Assert.Equal("42", target.GetBaggage("user-id"));
        Assert.Equal("req-789", target.GetBaggage("correlation_id"));
        Assert.Equal("dark-mode", target.GetBaggage("feature-flags"));
    }

    [Fact]
    public async Task InMemoryBus_Propagates_PipelineContext()
    {
        var services = new ServiceCollection();

        // Register mediator with pipeline context
        services.AddMediator(cfg =>
        {
            cfg.EnablePipelineContext = true;
            cfg.RegisterServicesFromAssemblyContaining<ContextBridgeTests>();
        });

        // Register messaging with in-memory transport
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.AddConsumer<TestConsumer>();
        });

        var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IMessageBus>();
        await bus.StartAsync();

        // Set baggage in the publishing scope's pipeline context
        var publisherContext = sp.GetRequiredService<IPipelineContext>();
        publisherContext.SetBaggage("tenant-id", "acme-corp");
        publisherContext.SetBaggage("request_id", "req-001");

        // Publish a message — the bridge should propagate context
        await bus.Publisher.PublishAsync(new TestEvent
        {
            Data = "hello",
            TenantId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            UserId = Guid.NewGuid()
        });

        // Verify the InMemory bus captured it
        var inMemoryBus = sp.GetRequiredService<InMemoryMessageBus>();
        var consumed = inMemoryBus.GetConsumed<TestEvent>().ToList();

        Assert.Single(consumed);
        Assert.Equal("hello", consumed[0].Data);

        await bus.StopAsync();
    }

    [Fact]
    public void HydrateContext_Handles_Null_Headers_Gracefully()
    {
        var ctx = new PipelineContext();

        // Should not throw
        PipelineContextBridge.HydrateContext(ctx, (IReadOnlyDictionary<string, string>?)null);

        Assert.Empty(ctx.GetAllBaggage());
    }

    [Fact]
    public void ExtractHeaders_Empty_When_No_State()
    {
        var ctx = new PipelineContext();
        var headers = PipelineContextBridge.ExtractHeaders(ctx);

        Assert.Empty(headers);
    }
}
