using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Conduit.Messaging.Registration;

/// <summary>
/// Health check that reports whether the message bus is connected.
/// </summary>
public sealed class MessagingHealthCheck(IMessageBus bus) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = bus.GetHealth();

        if (health.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy(health.Status));
        }

        return Task.FromResult(HealthCheckResult.Degraded(health.Status, data: health.Details is null
            ? null
            : new Dictionary<string, object>
            {
                ["host"] = health.Details.Host ?? "unknown",
                ["port"] = health.Details.Port ?? 0,
                ["started"] = health.Details.Started,
                ["consumers"] = health.Details.ConsumerCount
            }));
    }
}
