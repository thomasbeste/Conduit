using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class PrePostProcessorTests
{
    public record TrackedRequest(string Id) : IRequest<string>;

    public class TrackedRequestHandler : IRequestHandler<TrackedRequest, string>
    {
        public Task<string> Handle(TrackedRequest request, CancellationToken cancellationToken)
        {
            ExecutionLog.Add($"Handler:{request.Id}");
            return Task.FromResult($"Result:{request.Id}");
        }
    }

    public static List<string> ExecutionLog { get; } = [];

    public class PreProcessor : IRequestPreProcessor<TrackedRequest>
    {
        public Task Process(TrackedRequest request, CancellationToken cancellationToken)
        {
            ExecutionLog.Add($"Pre:{request.Id}");
            return Task.CompletedTask;
        }
    }

    public class PostProcessor : IRequestPostProcessor<TrackedRequest, string>
    {
        public Task Process(TrackedRequest request, string response, CancellationToken cancellationToken)
        {
            ExecutionLog.Add($"Post:{request.Id}:{response}");
            return Task.CompletedTask;
        }
    }

    public class GenericPreProcessor<TRequest> : IRequestPreProcessor<TRequest>
        where TRequest : notnull
    {
        public Task Process(TRequest request, CancellationToken cancellationToken)
        {
            ExecutionLog.Add($"GenericPre:{typeof(TRequest).Name}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PreProcessor_ExecutesBeforeHandler()
    {
        ExecutionLog.Clear();

        var services = new ServiceCollection();
        services.AddConduit(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PrePostProcessorTests>();
            cfg.AddPreProcessor<PreProcessor>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new TrackedRequest("test1"));

        Assert.Equal(2, ExecutionLog.Count);
        Assert.Equal("Pre:test1", ExecutionLog[0]);
        Assert.Equal("Handler:test1", ExecutionLog[1]);
    }

    [Fact]
    public async Task PostProcessor_ExecutesAfterHandler()
    {
        ExecutionLog.Clear();

        var services = new ServiceCollection();
        services.AddConduit(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PrePostProcessorTests>();
            cfg.AddPostProcessor<PostProcessor>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new TrackedRequest("test2"));

        Assert.Equal(2, ExecutionLog.Count);
        Assert.Equal("Handler:test2", ExecutionLog[0]);
        Assert.Equal("Post:test2:Result:test2", ExecutionLog[1]);
    }

    [Fact]
    public async Task PreAndPostProcessors_ExecuteInCorrectOrder()
    {
        ExecutionLog.Clear();

        var services = new ServiceCollection();
        services.AddConduit(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PrePostProcessorTests>();
            cfg.AddPreProcessor<PreProcessor>();
            cfg.AddPostProcessor<PostProcessor>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new TrackedRequest("test3"));

        Assert.Equal(3, ExecutionLog.Count);
        Assert.Equal("Pre:test3", ExecutionLog[0]);
        Assert.Equal("Handler:test3", ExecutionLog[1]);
        Assert.Equal("Post:test3:Result:test3", ExecutionLog[2]);
    }

    [Fact]
    public async Task OpenGenericPreProcessor_AppliesAllRequests()
    {
        ExecutionLog.Clear();

        var services = new ServiceCollection();
        services.AddConduit(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PrePostProcessorTests>();
            cfg.AddOpenPreProcessor(typeof(GenericPreProcessor<>));
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.Send(new TrackedRequest("test4"));

        Assert.Contains("GenericPre:TrackedRequest", ExecutionLog);
    }
}
