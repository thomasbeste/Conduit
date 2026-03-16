namespace Conduit;

/// <summary>
/// Delegate representing the next step in the pipeline (either another behavior or the handler).
/// </summary>
/// <typeparam name="TResponse">The response type.</typeparam>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>
/// Pipeline behavior that wraps request handling, enabling cross-cutting concerns.
/// Behaviors form a chain around the handler, similar to middleware in ASP.NET Core.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <remarks>
/// <para>
/// Behaviors are executed in registration order (first registered = outermost).
/// Each behavior can:
/// </para>
/// <list type="bullet">
///   <item>Execute logic before the handler by doing work before calling <c>next()</c></item>
///   <item>Execute logic after the handler by doing work after <c>await next()</c></item>
///   <item>Short-circuit the pipeline by returning without calling <c>next()</c></item>
///   <item>Transform the response by modifying the result of <c>next()</c></item>
///   <item>Handle exceptions by wrapping <c>next()</c> in try/catch</item>
/// </list>
///
/// <para><b>Example - Logging behavior:</b></para>
/// <code>
/// public class LoggingBehavior&lt;TRequest, TResponse&gt; : IPipelineBehavior&lt;TRequest, TResponse&gt;
///     where TRequest : notnull
/// {
///     public async Task&lt;TResponse&gt; Handle(TRequest request, RequestHandlerDelegate&lt;TResponse&gt; next, CancellationToken ct)
///     {
///         _logger.LogInformation("Handling {RequestType}", typeof(TRequest).Name);
///         var response = await next();
///         _logger.LogInformation("Handled {RequestType}", typeof(TRequest).Name);
///         return response;
///     }
/// }
/// </code>
///
/// <para><b>Example - Validation behavior (short-circuit):</b></para>
/// <code>
/// public class ValidationBehavior&lt;TRequest, TResponse&gt; : IPipelineBehavior&lt;TRequest, TResponse&gt;
///     where TRequest : notnull
/// {
///     public async Task&lt;TResponse&gt; Handle(TRequest request, RequestHandlerDelegate&lt;TResponse&gt; next, CancellationToken ct)
///     {
///         var failures = await _validator.ValidateAsync(request, ct);
///         if (failures.Any())
///             throw new ValidationException(failures);
///         return await next();
///     }
/// }
/// </code>
///
/// <para>Register behaviors using <see cref="ConduitConfiguration.AddBehavior{TBehavior}"/> for closed types
/// or <see cref="ConduitConfiguration.AddOpenBehavior"/> for open generic behaviors.</para>
/// </remarks>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Handles the request, optionally calling the next behavior/handler in the pipeline.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="next">Delegate to invoke the next behavior or handler. Must be called to continue the pipeline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the pipeline.</returns>
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
