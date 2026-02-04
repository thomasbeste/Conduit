using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit;

/// <summary>
/// Default implementation of <see cref="IDispatcher"/> that routes requests to their handlers
/// and orchestrates the pipeline (behaviors, pre/post-processors, exception handlers).
/// </summary>
/// <remarks>
/// The dispatcher uses reflection caching internally for performance. Handler wrapper types
/// are created once per request type and cached for the application lifetime.
/// </remarks>
public sealed class Dispatcher(IServiceProvider serviceProvider, INotificationPublisher? notificationPublisher = null) : IDispatcher
{
    private readonly INotificationPublisher _notificationPublisher = notificationPublisher ?? new ForeachAwaitPublisher();

    private static readonly ConcurrentDictionary<Type, RequestHandlerBase> RequestHandlers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> NotificationHandlers = new();
    private static readonly ConcurrentDictionary<Type, StreamRequestHandlerBase> StreamHandlers = new();

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = RequestHandlers.GetOrAdd(requestType, static t =>
        {
            var responseType = t.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                .GetGenericArguments()[0];

            var wrapperType = typeof(RequestHandlerWrapper<,>).MakeGenericType(t, responseType);
            return (RequestHandlerBase)Activator.CreateInstance(wrapperType)!;
        });

        var result = await handler.Handle(request, serviceProvider, cancellationToken).ConfigureAwait(false);
        return (TResponse)result!;
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = RequestHandlers.GetOrAdd(requestType, static t =>
        {
            var requestInterface = t.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                ?? throw new InvalidOperationException($"Type {t.Name} does not implement IRequest<TResponse>");

            var responseType = requestInterface.GetGenericArguments()[0];
            var wrapperType = typeof(RequestHandlerWrapper<,>).MakeGenericType(t, responseType);
            return (RequestHandlerBase)Activator.CreateInstance(wrapperType)!;
        });

        return handler.Handle(request, serviceProvider, cancellationToken);
    }

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var notificationType = notification.GetType();
        var handler = NotificationHandlers.GetOrAdd(notificationType, static t =>
        {
            var wrapperType = typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t);
            return (NotificationHandlerWrapper)Activator.CreateInstance(wrapperType)!;
        });

        return handler.Handle(notification, serviceProvider, _notificationPublisher, cancellationToken);
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        return Publish((object)notification, cancellationToken);
    }

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = StreamHandlers.GetOrAdd(requestType, static t =>
        {
            var responseType = t.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>))
                .GetGenericArguments()[0];

            var wrapperType = typeof(StreamRequestHandlerWrapper<,>).MakeGenericType(t, responseType);
            return (StreamRequestHandlerBase)Activator.CreateInstance(wrapperType)!;
        });

        return handler.Handle<TResponse>(request, serviceProvider, cancellationToken);
    }

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = StreamHandlers.GetOrAdd(requestType, static t =>
        {
            var streamInterface = t.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>))
                ?? throw new InvalidOperationException($"Type {t.Name} does not implement IStreamRequest<TResponse>");

            var responseType = streamInterface.GetGenericArguments()[0];
            var wrapperType = typeof(StreamRequestHandlerWrapper<,>).MakeGenericType(t, responseType);
            return (StreamRequestHandlerBase)Activator.CreateInstance(wrapperType)!;
        });

        return handler.Handle<object?>(request, serviceProvider, cancellationToken);
    }
}

internal abstract class RequestHandlerBase
{
    public abstract Task<object?> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerBase
    where TRequest : IRequest<TResponse>
{
    public override async Task<object?> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;

        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
            ?? throw new InvalidOperationException($"No handler registered for {typeof(TRequest).Name}");

        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse().ToArray();
        var preProcessors = serviceProvider.GetServices<IRequestPreProcessor<TRequest>>().ToArray();
        var postProcessors = serviceProvider.GetServices<IRequestPostProcessor<TRequest, TResponse>>().ToArray();
        var exceptionHandlers = serviceProvider.GetServices<IRequestExceptionHandler<TRequest, TResponse>>().ToArray();

        RequestHandlerDelegate<TResponse> handlerDelegate = () => handler.Handle(typedRequest, cancellationToken);

        // Wrap with post-processors (executed after handler)
        if (postProcessors.Length > 0)
        {
            var next = handlerDelegate;
            handlerDelegate = async () =>
            {
                var response = await next().ConfigureAwait(false);
                foreach (var processor in postProcessors)
                {
                    await processor.Process(typedRequest, response, cancellationToken).ConfigureAwait(false);
                }
                return response;
            };
        }

        // Wrap with pre-processors (executed before handler)
        if (preProcessors.Length > 0)
        {
            var next = handlerDelegate;
            handlerDelegate = async () =>
            {
                foreach (var processor in preProcessors)
                {
                    await processor.Process(typedRequest, cancellationToken).ConfigureAwait(false);
                }
                return await next().ConfigureAwait(false);
            };
        }

        // Wrap with behaviors (outermost layer)
        foreach (var behavior in behaviors)
        {
            var next = handlerDelegate;
            var currentBehavior = behavior;
            handlerDelegate = () => currentBehavior.Handle(typedRequest, next, cancellationToken);
        }

        // Wrap with exception handlers
        if (exceptionHandlers.Length > 0)
        {
            var next = handlerDelegate;
            handlerDelegate = async () =>
            {
                try
                {
                    return await next().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var state = new RequestExceptionHandlerState<TResponse>();

                    foreach (var exceptionHandler in exceptionHandlers)
                    {
                        await exceptionHandler.Handle(typedRequest, ex, state, cancellationToken).ConfigureAwait(false);

                        if (state.Handled)
                        {
                            return state.Response!;
                        }
                    }

                    throw;
                }
            };
        }

        return await handlerDelegate().ConfigureAwait(false);
    }
}

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(object notification, IServiceProvider serviceProvider, INotificationPublisher publisher, CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override Task Handle(object notification, IServiceProvider serviceProvider, INotificationPublisher publisher, CancellationToken cancellationToken)
    {
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>().ToArray();

        if (handlers.Length == 0)
        {
            return Task.CompletedTask;
        }

        return publisher.Publish(handlers, (TNotification)notification, cancellationToken);
    }
}

internal abstract class StreamRequestHandlerBase
{
    public abstract IAsyncEnumerable<TResponse> Handle<TResponse>(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class StreamRequestHandlerWrapper<TRequest, TResponse> : StreamRequestHandlerBase
    where TRequest : IStreamRequest<TResponse>
{
    public override async IAsyncEnumerable<TResult> Handle<TResult>(
        object request,
        IServiceProvider serviceProvider,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;

        var handler = serviceProvider.GetService<IStreamRequestHandler<TRequest, TResponse>>()
            ?? throw new InvalidOperationException($"No stream handler registered for {typeof(TRequest).Name}");

        var behaviors = serviceProvider.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>().Reverse().ToArray();

        StreamHandlerDelegate<TResponse> handlerDelegate = () => handler.Handle(typedRequest, cancellationToken);

        foreach (var behavior in behaviors)
        {
            var next = handlerDelegate;
            var currentBehavior = behavior;
            handlerDelegate = () => currentBehavior.Handle(typedRequest, next, cancellationToken);
        }

        await foreach (var item in handlerDelegate().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return (TResult)(object)item!;
        }
    }
}
