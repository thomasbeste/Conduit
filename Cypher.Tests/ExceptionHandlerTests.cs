using Microsoft.Extensions.DependencyInjection;

namespace Cypher.Tests;

public class ExceptionHandlerTests
{
    public record FlakyRequest(bool ShouldFail) : IRequest<string>;

    public class FlakyRequestHandler : IRequestHandler<FlakyRequest, string>
    {
        public Task<string> Handle(FlakyRequest request, CancellationToken cancellationToken)
        {
            if (request.ShouldFail)
            {
                throw new InvalidOperationException("This is fine. Everything is fine.");
            }

            return Task.FromResult("Success");
        }
    }

    public class InvalidOperationExceptionHandler : IRequestExceptionHandler<FlakyRequest, string, InvalidOperationException>
    {
        public Task Handle(
            FlakyRequest request,
            InvalidOperationException exception,
            RequestExceptionHandlerState<string> state,
            CancellationToken cancellationToken)
        {
            state.SetHandled("Recovered from: " + exception.Message);
            return Task.CompletedTask;
        }
    }

    public class GenericExceptionHandler : IRequestExceptionHandler<FlakyRequest, string>
    {
        public static int HandleCount { get; set; }

        public Task Handle(
            FlakyRequest request,
            Exception exception,
            RequestExceptionHandlerState<string> state,
            CancellationToken cancellationToken)
        {
            HandleCount++;
            // Don't handle - let it propagate
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExceptionHandler_HandlesException_ReturnsRecoveredResponse()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ExceptionHandlerTests>();
            cfg.AddExceptionHandler<InvalidOperationExceptionHandler>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new FlakyRequest(ShouldFail: true));

        Assert.StartsWith("Recovered from:", result);
    }

    [Fact]
    public async Task ExceptionHandler_NoException_ReturnsNormalResponse()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ExceptionHandlerTests>();
            cfg.AddExceptionHandler<InvalidOperationExceptionHandler>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new FlakyRequest(ShouldFail: false));

        Assert.Equal("Success", result);
    }

    [Fact]
    public async Task ExceptionHandler_DoesNotHandle_PropagatesException()
    {
        GenericExceptionHandler.HandleCount = 0;

        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ExceptionHandlerTests>();
            cfg.AddExceptionHandler<GenericExceptionHandler>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.Send(new FlakyRequest(ShouldFail: true)));

        Assert.Equal(1, GenericExceptionHandler.HandleCount);
    }
}
