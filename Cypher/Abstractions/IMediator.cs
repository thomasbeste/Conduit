namespace Cypher;

public interface IDispatcher : ISender, IPublisher
{
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<object?> CreateStream(
        object request,
        CancellationToken cancellationToken = default);
}
