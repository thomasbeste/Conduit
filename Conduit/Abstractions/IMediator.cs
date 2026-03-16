namespace Conduit.Mediator;

/// <summary>
/// Combines ISender and IPublisher for request dispatch and notification publishing.
/// Provided for compatibility with codebases migrating from MediatR.
/// </summary>
public interface IMediator : ISender, IPublisher;

/// <summary>
/// Full dispatcher interface adding streaming support on top of IMediator.
/// </summary>
public interface IDispatcher : IMediator
{
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<object?> CreateStream(
        object request,
        CancellationToken cancellationToken = default);
}
