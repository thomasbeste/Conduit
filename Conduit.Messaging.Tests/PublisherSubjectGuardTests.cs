using Conduit.Messaging.AzureServiceBus;
using Xunit;

namespace Conduit.Messaging.Tests;

/// <summary>
/// Pure-logic tests for the publish-side topology guard. The guard is
/// the publish-time equivalent of <see cref="AzureServiceBusMessageBus.VerifyTopologyAsync"/>
/// — refuses to send a Subject that has no matching subscription
/// correlation filter, because the broker silently discards such
/// messages without raising any client-visible error.
///
/// These tests drive <see cref="AzureServiceBusMessageBus.BuildPublisherGuard"/>
/// (pure) and <see cref="PublisherSubjectGuard.EnsureCanPublish"/>
/// directly with canned topology so we don't need real ASB to exercise
/// every shape of subscription filter.
/// </summary>
public class PublisherSubjectGuardTests
{
    private const string Topic = "gpi-events";

    [Fact]
    public void Subject_With_Matching_Correlation_Filter_Is_Allowed()
    {
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, [
            new("service-docparser-parsedocumentcommand",
                RuleFilterKind.Correlation, "ParseDocumentCommand"),
        ]);

        // No throw = allowed.
        guard.EnsureCanPublish("ParseDocumentCommand");
        Assert.Contains("ParseDocumentCommand", guard.KnownSubjects);
        Assert.False(guard.HasWildcardSubscription);
    }

    [Fact]
    public void Subject_With_No_Matching_Filter_Throws_Loud()
    {
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, [
            new("service-pii-detectpiibatchcommand",
                RuleFilterKind.Correlation, "DetectPiiBatchCommand"),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            guard.EnsureCanPublish("ParseDocumentCommand"));

        // Operator-actionable: names the subject + topic + names the recovery.
        Assert.Contains("ParseDocumentCommand", ex.Message);
        Assert.Contains(Topic, ex.Message);
        Assert.Contains("silently discarded", ex.Message);
        Assert.Contains("post-update reconciler", ex.Message);
    }

    [Fact]
    public void True_Filter_Is_Treated_As_Wildcard_And_Allows_Everything()
    {
        // The default $Default rule that ships with every new subscription
        // is a TrueRuleFilter. We must NOT block when one of those is
        // present — it'd be a false positive on any custom routing.
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, [
            new("catch-all", RuleFilterKind.True, null),
            new("service-pii-detectpiibatchcommand",
                RuleFilterKind.Correlation, "DetectPiiBatchCommand"),
        ]);

        Assert.True(guard.HasWildcardSubscription);
        // No throw for an "unknown" subject — wildcard might catch it.
        guard.EnsureCanPublish("SomeSubjectNobodyDeclared");
    }

    [Fact]
    public void Sql_Filter_Is_Treated_As_Wildcard()
    {
        // SQL filters can route by arbitrary expressions we can't evaluate
        // statically (e.g. "user.role = 'admin'"). Be permissive so we
        // never produce a false-positive block.
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, [
            new("custom-route", RuleFilterKind.Sql, null),
        ]);

        Assert.True(guard.HasWildcardSubscription);
        guard.EnsureCanPublish("AnyEvent");
    }

    [Fact]
    public void False_Filter_Does_Not_Count_As_Subscriber()
    {
        // FalseRuleFilter is explicit-drop. A subscription with ONLY a
        // false filter cannot receive anything — must not be counted
        // as a subscriber for any subject.
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, [
            new("drop-sink", RuleFilterKind.False, null),
        ]);

        Assert.False(guard.HasWildcardSubscription);
        Assert.Empty(guard.KnownSubjects);
        Assert.Throws<InvalidOperationException>(() =>
            guard.EnsureCanPublish("AnyEvent"));
    }

    [Fact]
    public void Multiple_Subscriptions_Same_Subject_Coalesce()
    {
        // Two services consume the same subject — set semantics, not list.
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, [
            new("service-a-fooevent", RuleFilterKind.Correlation, "FooEvent"),
            new("service-b-fooevent", RuleFilterKind.Correlation, "FooEvent"),
        ]);

        Assert.Single(guard.KnownSubjects);
        guard.EnsureCanPublish("FooEvent");
    }

    [Fact]
    public void Empty_Topology_Blocks_Every_Subject()
    {
        // No subscriptions at all = nothing routes. Every publish must throw.
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, []);

        Assert.Empty(guard.KnownSubjects);
        Assert.False(guard.HasWildcardSubscription);
        Assert.Throws<InvalidOperationException>(() =>
            guard.EnsureCanPublish("AnythingAtAll"));
    }

    [Fact]
    public void Correlation_Filter_With_Null_Subject_Is_Ignored()
    {
        // CorrelationRuleFilter without an explicit Subject doesn't add
        // anything routable — neither blocks publishes nor wildcards.
        // Some legacy subscriptions are shaped this way (filter on other
        // properties only).
        var guard = AzureServiceBusMessageBus.BuildPublisherGuard(Topic, [
            new("legacy", RuleFilterKind.Correlation, null),
        ]);

        Assert.Empty(guard.KnownSubjects);
        Assert.False(guard.HasWildcardSubscription);
    }
}
