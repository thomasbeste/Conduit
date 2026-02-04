namespace Cypher;

/// <summary>
/// Post-processor that runs after the request handler completes successfully.
/// Use for cleanup, response enrichment, or logging that needs access to the response.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <remarks>
/// <para>
/// Post-processors are simpler than behaviors when you only need "after" logic.
/// They cannot modify the response (it's passed as-is to the next post-processor).
/// </para>
///
/// <para><b>Execution order:</b> All post-processors run after the handler but inside behaviors.</para>
///
/// <para><b>Example - Response caching:</b></para>
/// <code>
/// public class CachingPostProcessor&lt;TRequest, TResponse&gt; : IRequestPostProcessor&lt;TRequest, TResponse&gt;
///     where TRequest : notnull
/// {
///     public Task Process(TRequest request, TResponse response, CancellationToken ct)
///     {
///         var key = GetCacheKey(request);
///         _cache.Set(key, response);
///         return Task.CompletedTask;
///     }
/// }
/// </code>
///
/// <para>Register using <see cref="CypherConfiguration.AddPostProcessor{TPostProcessor}"/> or
/// <see cref="CypherConfiguration.AddOpenPostProcessor"/>.</para>
/// </remarks>
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Processes the request and response after the handler completes.
    /// </summary>
    /// <param name="request">The original request.</param>
    /// <param name="response">The response from the handler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
