using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class BasicRequestTests
{
    public record Ping(string Message) : IRequest<Pong>;
    public record Pong(string Reply);

    public class PingHandler : IRequestHandler<Ping, Pong>
    {
        public Task<Pong> Handle(Ping request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new Pong($"Pong: {request.Message}"));
        }
    }

    public record VoidCommand(string Value) : IRequest;

    public class VoidCommandHandler : IRequestHandler<VoidCommand>
    {
        public static string? LastValue { get; private set; }

        public Task<Unit> Handle(VoidCommand request, CancellationToken cancellationToken)
        {
            LastValue = request.Value;
            return Task.FromResult(Unit.Value);
        }
    }

    [Fact]
    public async Task Send_WithResponse_ReturnsExpectedResult()
    {
        var services = new ServiceCollection();
        services.AddConduit(cfg => cfg.RegisterServicesFromAssemblyContaining<BasicRequestTests>());

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.Send(new Ping("Hello"));

        Assert.Equal("Pong: Hello", response.Reply);
    }

    [Fact]
    public async Task Send_VoidRequest_ReturnsUnit()
    {
        var services = new ServiceCollection();
        services.AddConduit(cfg => cfg.RegisterServicesFromAssemblyContaining<BasicRequestTests>());

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new VoidCommand("test-value"));

        Assert.Equal(Unit.Value, result);
        Assert.Equal("test-value", VoidCommandHandler.LastValue);
    }

    [Fact]
    public async Task Send_Untyped_ReturnsResponse()
    {
        var services = new ServiceCollection();
        services.AddConduit(cfg => cfg.RegisterServicesFromAssemblyContaining<BasicRequestTests>());

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        object request = new Ping("Untyped");
        var response = await dispatcher.Send(request);

        Assert.IsType<Pong>(response);
        Assert.Equal("Pong: Untyped", ((Pong)response!).Reply);
    }

    [Fact]
    public async Task Send_NoHandler_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        services.AddConduit(_ => { }); // No handlers registered

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Send(new Ping("Will fail")));
    }
}
