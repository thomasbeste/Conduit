using Microsoft.Extensions.DependencyInjection;

namespace Cypher.Tests;

public class PipelineContextTests
{
    public record TimedRequest(string Name) : IRequest<string>;

    public class TimedRequestHandler : IRequestHandler<TimedRequest, string>
    {
        public Task<string> Handle(TimedRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult($"Hello, {request.Name}");
        }
    }

    public record CounterRequest(int Value) : IRequest<int>;

    public class CounterRequestHandler : IRequestHandler<CounterRequest, int>
    {
        public Task<int> Handle(CounterRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.Value * 2);
        }
    }

    /// <summary>
    /// Example behavior that uses optional IPipelineContext for timing.
    /// Uses IEnumerable for optional injection (empty if not registered).
    /// </summary>
    public class TimingBehavior<TRequest, TResponse>(IEnumerable<IPipelineContext> contexts)
        : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        private readonly IPipelineContext? _context = contexts.FirstOrDefault();

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (_context is null)
            {
                return await next().ConfigureAwait(false);
            }

            using var timer = _context.StartTimer(typeof(TRequest).Name);
            return await next().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Example behavior that increments a counter.
    /// </summary>
    public class CountingBehavior<TRequest, TResponse>(IEnumerable<IPipelineContext> contexts)
        : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        private readonly IPipelineContext? _context = contexts.FirstOrDefault();

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            _context?.Increment("requests");
            return await next().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task SingleRequest_RecordsTiming()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineContextTests>();
            cfg.AddOpenBehavior(typeof(TimingBehavior<,>));
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var context = scope.ServiceProvider.GetRequiredService<IPipelineContext>();

        await dispatcher.Send(new TimedRequest("Jian Yang"));

        var timings = context.GetTimings();
        Assert.Single(timings);
        Assert.Equal("TimedRequest", timings[0].Name);
        Assert.True(timings[0].Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task MultipleRequests_AccumulateTimings()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineContextTests>();
            cfg.AddOpenBehavior(typeof(TimingBehavior<,>));
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var context = scope.ServiceProvider.GetRequiredService<IPipelineContext>();

        await dispatcher.Send(new TimedRequest("Erlich"));
        await dispatcher.Send(new TimedRequest("Gilfoyle"));
        await dispatcher.Send(new CounterRequest(42));

        var timings = context.GetTimings();
        Assert.Equal(3, timings.Count);
        Assert.Equal(2, timings.Count(t => t.Name == "TimedRequest"));
        Assert.Single(timings, t => t.Name == "CounterRequest");
    }

    [Fact]
    public async Task Increment_AccumulatesCounter()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineContextTests>();
            cfg.AddOpenBehavior(typeof(CountingBehavior<,>));
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var context = scope.ServiceProvider.GetRequiredService<IPipelineContext>();

        await dispatcher.Send(new TimedRequest("Dinesh"));
        await dispatcher.Send(new TimedRequest("Richard"));
        await dispatcher.Send(new CounterRequest(10));

        var metrics = context.GetMetrics();
        Assert.True(metrics.ContainsKey("requests"));
        Assert.Equal(3, metrics["requests"].Count);
    }

    [Fact]
    public void Record_TracksMinMaxTotalAverage()
    {
        var context = new PipelineContext();

        context.Record("response_time", 100);
        context.Record("response_time", 200);
        context.Record("response_time", 150);

        var metrics = context.GetMetrics();
        var metric = metrics["response_time"];

        Assert.Equal(3, metric.Count);
        Assert.Equal(450, metric.Total);
        Assert.Equal(100, metric.Min);
        Assert.Equal(200, metric.Max);
        Assert.Equal(150, metric.Average);
    }

    [Fact]
    public void Items_StoresArbitraryData()
    {
        var context = new PipelineContext();

        context.Items["user_id"] = "jian_yang";
        context.Items["request_count"] = 42;
        context.Items["nullable_value"] = null;

        Assert.Equal("jian_yang", context.Items["user_id"]);
        Assert.Equal(42, context.Items["request_count"]);
        Assert.Null(context.Items["nullable_value"]);
    }

    [Fact]
    public async Task DifferentScopes_HaveSeparateContexts()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineContextTests>();
            cfg.AddOpenBehavior(typeof(TimingBehavior<,>));
        });

        var provider = services.BuildServiceProvider();

        IPipelineContext context1;
        IPipelineContext context2;

        using (var scope1 = provider.CreateScope())
        {
            var dispatcher = scope1.ServiceProvider.GetRequiredService<IDispatcher>();
            context1 = scope1.ServiceProvider.GetRequiredService<IPipelineContext>();

            await dispatcher.Send(new TimedRequest("Scope1"));
        }

        using (var scope2 = provider.CreateScope())
        {
            var dispatcher = scope2.ServiceProvider.GetRequiredService<IDispatcher>();
            context2 = scope2.ServiceProvider.GetRequiredService<IPipelineContext>();

            await dispatcher.Send(new TimedRequest("Scope2A"));
            await dispatcher.Send(new TimedRequest("Scope2B"));
        }

        Assert.Single(context1.GetTimings());
        Assert.Equal(2, context2.GetTimings().Count);
    }

    [Fact]
    public async Task DisabledPipelineContext_BehaviorHandlesNullGracefully()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineContextTests>();
            cfg.EnablePipelineContext = false;
            cfg.AddOpenBehavior(typeof(TimingBehavior<,>));
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        // Should not throw even though context is null
        var result = await dispatcher.Send(new TimedRequest("NoContext"));

        Assert.Equal("Hello, NoContext", result);
    }

    [Fact]
    public async Task ThreadSafety_ParallelSendCalls_DoNotCorruptState()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PipelineContextTests>();
            cfg.AddOpenBehavior(typeof(TimingBehavior<,>));
            cfg.AddOpenBehavior(typeof(CountingBehavior<,>));
        });

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var context = scope.ServiceProvider.GetRequiredService<IPipelineContext>();

        const int taskCount = 100;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(i => dispatcher.Send(new TimedRequest($"Parallel{i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        var timings = context.GetTimings();
        var metrics = context.GetMetrics();

        Assert.Equal(taskCount, timings.Count);
        Assert.Equal(taskCount, metrics["requests"].Count);
    }

    [Fact]
    public void TimerScope_StopIsIdempotent()
    {
        var context = new PipelineContext();

        using (var timer = context.StartTimer("test"))
        {
            timer.Stop();
            timer.Stop(); // Should not throw or double-record
        }

        Assert.Single(context.GetTimings());
    }

    [Fact]
    public void TimerScope_ExposesElapsedWhileRunning()
    {
        var context = new PipelineContext();

        using var timer = context.StartTimer("running");

        Thread.Sleep(10);
        var elapsed1 = timer.Elapsed;

        Thread.Sleep(10);
        var elapsed2 = timer.Elapsed;

        Assert.True(elapsed2 > elapsed1);
        Assert.Equal("running", timer.Name);
    }
}
