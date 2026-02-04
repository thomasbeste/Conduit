namespace Conduit;

/// <summary>
/// Publishes notifications to all handlers in parallel using <see cref="Task.WhenAll"/>.
/// Provides better throughput when handlers are independent and can execute concurrently.
/// </summary>
/// <remarks>
/// Use this publisher when:
/// - Handlers are independent and don't share mutable state
/// - You want maximum throughput for I/O-bound handlers
/// - Handler execution order doesn't matter
///
/// Caution: If any handler throws, the exception will be wrapped in an <see cref="AggregateException"/>.
/// For sequential execution, use <see cref="ForeachAwaitPublisher"/> instead.
/// </remarks>
public sealed class TaskWhenAllPublisher : INotificationPublisher
{
    public Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var tasks = handlers.Select(h => h.Handle(notification, cancellationToken));
        return Task.WhenAll(tasks);
    }
}
