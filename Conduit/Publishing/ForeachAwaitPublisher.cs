namespace Conduit;

/// <summary>
/// Publishes notifications to handlers sequentially, awaiting each handler before proceeding to the next.
/// This is the safest default strategy as it preserves ordering and makes debugging easier.
/// </summary>
/// <remarks>
/// Use this publisher when:
/// - Handler execution order matters
/// - You need predictable, sequential exception propagation
/// - Debugging/tracing requires clear causality
///
/// For parallel execution, use <see cref="TaskWhenAllPublisher"/> instead.
/// </remarks>
public sealed class ForeachAwaitPublisher : INotificationPublisher
{
    public async Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        foreach (var handler in handlers)
        {
            await handler.Handle(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
