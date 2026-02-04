namespace Cypher;

/// <summary>
/// Pre-processor that runs before the request handler.
/// Use for setup, validation, or logging that doesn't need to wrap the handler.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <remarks>
/// <para>
/// Pre-processors are simpler than behaviors when you only need "before" logic.
/// They cannot short-circuit the pipeline or access the response.
/// </para>
///
/// <para><b>Execution order:</b> All pre-processors run before behaviors and the handler.</para>
///
/// <para><b>Example - Audit logging:</b></para>
/// <code>
/// public class AuditPreProcessor&lt;TRequest&gt; : IRequestPreProcessor&lt;TRequest&gt;
///     where TRequest : notnull
/// {
///     public Task Process(TRequest request, CancellationToken ct)
///     {
///         _auditLog.LogRequest(typeof(TRequest).Name, request);
///         return Task.CompletedTask;
///     }
/// }
/// </code>
///
/// <para>Register using <see cref="CypherConfiguration.AddPreProcessor{TPreProcessor}"/> or
/// <see cref="CypherConfiguration.AddOpenPreProcessor"/>.</para>
/// </remarks>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : notnull
{
    /// <summary>
    /// Processes the request before it reaches the handler.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task Process(TRequest request, CancellationToken cancellationToken);
}
