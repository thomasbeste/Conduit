using Microsoft.Extensions.DependencyInjection;

namespace Cypher.Tests;

public class PipelineBehaviorTests
{
    public record GetValue(int Input) : IRequest<int>;

    public class GetValueHandler : IRequestHandler<GetValue, int>
    {
        public Task<int> Handle(GetValue request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.Input);
        }
    }

    public class DoubleItBehavior : IPipelineBehavior<GetValue, int>
    {
        public async Task<int> Handle(GetValue request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            var result = await next();
            return result * 2;
        }
    }

    public class AddTenBehavior : IPipelineBehavior<GetValue, int>
    {
        public async Task<int> Handle(GetValue request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            var result = await next();
            return result + 10;
        }
    }

    public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public static List<string> Logs { get; } = [];

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            Logs.Add($"Handling {typeof(TRequest).Name}");
            var response = await next();
            Logs.Add($"Handled {typeof(TRequest).Name}");
            return response;
        }
    }

    [Fact]
    public async Task Pipeline_SingleBehavior_TransformsResult()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>();
            cfg.AddBehavior<DoubleItBehavior>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new GetValue(5));

        Assert.Equal(10, result); // 5 * 2
    }

    [Fact]
    public async Task Pipeline_MultipleBehaviors_ExecuteInOrder()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>();
            cfg.AddBehavior<DoubleItBehavior>();  // Outer (last in chain): (result + 10) * 2
            cfg.AddBehavior<AddTenBehavior>();    // Inner (first in chain): result + 10
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new GetValue(5));

        // Handler returns 5
        // AddTenBehavior: 5 + 10 = 15
        // DoubleItBehavior: 15 * 2 = 30
        Assert.Equal(30, result);
    }

    [Fact]
    public async Task Pipeline_OpenGenericBehavior_AppliesAllRequests()
    {
        LoggingBehavior<GetValue, int>.Logs.Clear();

        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineBehaviorTests>();
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new GetValue(42));

        Assert.Equal(2, LoggingBehavior<GetValue, int>.Logs.Count);
        Assert.Contains("Handling", LoggingBehavior<GetValue, int>.Logs[0]);
        Assert.Contains("Handled", LoggingBehavior<GetValue, int>.Logs[1]);
    }
}
