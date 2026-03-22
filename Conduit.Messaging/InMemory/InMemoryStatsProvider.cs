namespace Conduit.Messaging.InMemory;

/// <summary>
/// Stats provider for in-memory transport (testing/development).
/// </summary>
public class InMemoryStatsProvider : IMessagingStatsProvider
{
    public Task<MessagingStats> GetStatsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new MessagingStats
        {
            Timestamp = DateTime.UtcNow,
            Transport = "InMemory",
            Queues = []
        });
}
