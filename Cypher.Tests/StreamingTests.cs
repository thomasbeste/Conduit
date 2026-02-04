using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Cypher.Tests;

public class StreamingTests
{
    public record CountToN(int N) : IStreamRequest<int>;

    public class CountToNHandler : IStreamRequestHandler<CountToN, int>
    {
        public async IAsyncEnumerable<int> Handle(
            CountToN request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int i = 1; i <= request.N; i++)
            {
                await Task.Delay(1, cancellationToken);
                yield return i;
            }
        }
    }

    public class DoublingStreamBehavior : IStreamPipelineBehavior<CountToN, int>
    {
        public async IAsyncEnumerable<int> Handle(
            CountToN request,
            StreamHandlerDelegate<int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next().WithCancellation(cancellationToken))
            {
                yield return item * 2;
            }
        }
    }

    [Fact]
    public async Task CreateStream_ReturnsAsyncEnumerable()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => cfg.RegisterServicesFromAssemblyContaining<StreamingTests>());

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var results = new List<int>();
        await foreach (var item in dispatcher.CreateStream(new CountToN(5)))
        {
            results.Add(item);
        }

        Assert.Equal([1, 2, 3, 4, 5], results);
    }

    [Fact]
    public async Task CreateStream_WithBehavior_TransformsResults()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<StreamingTests>();
            cfg.AddStreamBehavior<DoublingStreamBehavior>();
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var results = new List<int>();
        await foreach (var item in dispatcher.CreateStream(new CountToN(3)))
        {
            results.Add(item);
        }

        Assert.Equal([2, 4, 6], results);
    }

    [Fact]
    public async Task CreateStream_SupportsCancellation()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => cfg.RegisterServicesFromAssemblyContaining<StreamingTests>());

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        using var cts = new CancellationTokenSource();
        var results = new List<int>();

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await foreach (var item in dispatcher.CreateStream(new CountToN(100), cts.Token))
            {
                results.Add(item);
                if (results.Count >= 3)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.True(results.Count >= 3);
        Assert.True(results.Count < 100);
    }
}
