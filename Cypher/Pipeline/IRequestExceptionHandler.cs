namespace Cypher;

/// <summary>
/// Handles exceptions thrown during request processing.
/// Exception handlers form a chain - if one handler sets <see cref="RequestExceptionHandlerState{TResponse}.Handled"/>,
/// subsequent handlers are skipped and the response is returned.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <remarks>
/// <para>
/// Exception handlers provide a structured way to recover from errors without try/catch in every handler.
/// They can either:
/// </para>
/// <list type="bullet">
///   <item>Handle the exception by setting <c>state.SetHandled(response)</c> - returns the recovery response</item>
///   <item>Ignore the exception by not calling <c>SetHandled</c> - passes to next handler or rethrows</item>
/// </list>
///
/// <para><b>Example - Generic error recovery:</b></para>
/// <code>
/// public class ErrorRecoveryHandler&lt;TRequest, TResponse&gt; : IRequestExceptionHandler&lt;TRequest, TResponse&gt;
///     where TRequest : notnull
///     where TResponse : IResult, new()
/// {
///     public Task Handle(TRequest request, Exception ex, RequestExceptionHandlerState&lt;TResponse&gt; state, CancellationToken ct)
///     {
///         _logger.LogError(ex, "Request failed");
///         state.SetHandled(new TResponse { Success = false, Error = ex.Message });
///         return Task.CompletedTask;
///     }
/// }
/// </code>
///
/// <para>For type-specific exception handling, use <see cref="IRequestExceptionHandler{TRequest,TResponse,TException}"/>.</para>
///
/// <para>Register using <see cref="CypherConfiguration.AddExceptionHandler{TExceptionHandler}"/> or
/// <see cref="CypherConfiguration.AddOpenExceptionHandler"/>.</para>
/// </remarks>
public interface IRequestExceptionHandler<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Handles an exception that occurred during request processing.
    /// </summary>
    /// <param name="request">The original request.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="state">State object - call <see cref="RequestExceptionHandlerState{TResponse}.SetHandled"/> to provide a recovery response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task Handle(TRequest request, Exception exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);
}

/// <summary>
/// Handles exceptions of a specific type thrown during request processing.
/// Provides type-safe access to the exception without manual casting.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <typeparam name="TException">The specific exception type to handle.</typeparam>
/// <remarks>
/// <para>
/// This interface automatically filters exceptions - the <see cref="Handle(TRequest,TException,RequestExceptionHandlerState{TResponse},CancellationToken)"/>
/// method is only called when the exception is of type <typeparamref name="TException"/> or a derived type.
/// </para>
///
/// <para><b>Example - Handle specific exception:</b></para>
/// <code>
/// public class NotFoundHandler&lt;TRequest, TResponse&gt;
///     : IRequestExceptionHandler&lt;TRequest, TResponse, NotFoundException&gt;
///     where TRequest : notnull
///     where TResponse : IResult, new()
/// {
///     public Task Handle(TRequest request, NotFoundException ex, RequestExceptionHandlerState&lt;TResponse&gt; state, CancellationToken ct)
///     {
///         state.SetHandled(new TResponse { Success = false, Error = "Resource not found" });
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </remarks>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException> : IRequestExceptionHandler<TRequest, TResponse>
    where TRequest : notnull
    where TException : Exception
{
    /// <summary>
    /// Handles a specific exception type that occurred during request processing.
    /// </summary>
    /// <param name="request">The original request.</param>
    /// <param name="exception">The typed exception that was thrown.</param>
    /// <param name="state">State object - call <see cref="RequestExceptionHandlerState{TResponse}.SetHandled"/> to provide a recovery response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task Handle(TRequest request, TException exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);

    /// <summary>
    /// Routes the exception to the typed handler if it matches <typeparamref name="TException"/>.
    /// </summary>
    Task IRequestExceptionHandler<TRequest, TResponse>.Handle(
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken)
    {
        if (exception is TException typedException)
        {
            return Handle(request, typedException, state, cancellationToken);
        }

        return Task.CompletedTask;
    }
}
