namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Refuses to publish a message whose Subject has no matching subscription
/// correlation-filter on the configured topic. Without this guard the
/// broker accepts the message, finds no routing match, and silently
/// discards it — the publish-side equivalent of the consumer-side
/// silent-zombie pattern that lazy auto-create produced
/// (2026-05-15 incident).
///
/// The guard is built once at bus startup from the live topic topology
/// and never refreshed mid-process. Deploys restart pods and so re-read
/// topology; subscriptions added at runtime by some other process won't
/// be visible until restart, but the trade-off (read-once, zero per-
/// publish cost) is worth it because mid-flight subscription churn is
/// the operator's failure mode that <see cref="VerifyTopologyAsync"/>
/// already covers from the consumer side.
///
/// A subscription with a <see cref="RuleFilterKind.True"/> filter
/// (default <c>$Default</c> rule that ships with every new subscription)
/// or a <see cref="RuleFilterKind.Sql"/> filter (which we cannot evaluate
/// statically) flips <see cref="HasWildcardSubscription"/> on — that
/// turns the guard into a no-op for all subjects, so the deliberate
/// "catch everything" topology is never blocked. Only the case where
/// EVERY subscription is specifically correlation-filtered AND none
/// names this subject can produce a refusal.
/// </summary>
public sealed record PublisherSubjectGuard(
    IReadOnlySet<string> KnownSubjects,
    bool HasWildcardSubscription,
    string TopicName)
{
    /// <summary>
    /// Throws if no subscription on <see cref="TopicName"/> has a
    /// correlation filter for <paramref name="messageTypeName"/> AND no
    /// wildcard / SQL subscription would catch it. Otherwise no-op.
    /// </summary>
    public void EnsureCanPublish(string messageTypeName)
    {
        if (HasWildcardSubscription) return;
        if (KnownSubjects.Contains(messageTypeName)) return;
        throw new InvalidOperationException(
            $"Refusing to publish '{messageTypeName}' to topic '{TopicName}': " +
            "no subscription has a correlation filter matching this Subject. " +
            "The message would be silently discarded by the broker. " +
            "Re-run the post-update reconciler against each consumer service's " +
            "subscription manifest, then restart this service so it re-reads " +
            "the topology. This is a deploy / topology bug, not a transient error.");
    }
}

/// <summary>
/// Kind of subscription rule, summarised for the topology guard.
/// </summary>
public enum RuleFilterKind
{
    /// <summary>Correlation filter with a (possibly null) Subject value.</summary>
    Correlation,

    /// <summary>Matches everything — the default <c>$Default</c> rule.</summary>
    True,

    /// <summary>SQL filter — semantically opaque to the guard.</summary>
    Sql,

    /// <summary>Matches nothing — explicit drop.</summary>
    False,
}

/// <summary>
/// Per-subscription summary feeding <see cref="PublisherSubjectGuard"/>.
/// </summary>
public sealed record SubscriptionRuleSummary(
    string SubscriptionName,
    RuleFilterKind Kind,
    string? CorrelationSubject);
