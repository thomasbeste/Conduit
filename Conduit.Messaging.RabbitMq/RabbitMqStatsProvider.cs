using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// Fetches queue statistics from the RabbitMQ Management API.
/// </summary>
public class RabbitMqStatsProvider(
    RabbitMqSettings settings,
    ILogger<RabbitMqStatsProvider> logger) : IMessagingStatsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public async Task<MessagingStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var managementPort = 15672; // RabbitMQ management plugin default
            var encodedVhost = Uri.EscapeDataString(settings.VirtualHost);

            using var httpClient = new HttpClient();
            var auth = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{settings.Username}:{settings.Password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            var queuesUrl = $"http://{settings.Host}:{managementPort}/api/queues/{encodedVhost}";
            logger.LogDebug("Fetching RabbitMQ queues from {Url}", queuesUrl);

            var response = await httpClient.GetAsync(queuesUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var queuesJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var queues = JsonSerializer.Deserialize<List<RabbitMqQueue>>(queuesJson, JsonOptions) ?? [];

            return new MessagingStats
            {
                Timestamp = DateTime.UtcNow,
                Transport = "RabbitMQ",
                Queues = queues.Select(q => new QueueStats
                {
                    Name = q.Name,
                    MessagesReady = q.MessagesReady,
                    MessagesUnacknowledged = q.MessagesUnacknowledged,
                    TotalMessages = q.Messages,
                    Consumers = q.Consumers,
                    MessageStats = new MessageRateStats
                    {
                        PublishRate = q.MessageStats?.PublishDetails?.Rate ?? 0,
                        DeliverRate = q.MessageStats?.DeliverGetDetails?.Rate ?? 0,
                        AckRate = q.MessageStats?.AckDetails?.Rate ?? 0,
                        TotalPublished = q.MessageStats?.Publish ?? 0,
                        TotalDelivered = q.MessageStats?.DeliverGet ?? 0,
                        TotalAcknowledged = q.MessageStats?.Ack ?? 0
                    },
                    State = q.State,
                    IdleSince = q.IdleSince,
                    Memory = q.Memory
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch RabbitMQ messaging stats");
            return new MessagingStats
            {
                Timestamp = DateTime.UtcNow,
                Transport = "RabbitMQ",
                Queues = []
            };
        }
    }
}

// RabbitMQ Management API response models
internal class RabbitMqQueue
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("messages")]
    public long Messages { get; set; }
    [JsonPropertyName("messages_ready")]
    public long MessagesReady { get; set; }
    [JsonPropertyName("messages_unacknowledged")]
    public long MessagesUnacknowledged { get; set; }
    [JsonPropertyName("consumers")]
    public int Consumers { get; set; }
    [JsonPropertyName("message_stats")]
    public RabbitMqMessageStats? MessageStats { get; set; }
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
    [JsonPropertyName("idle_since")]
    public string? IdleSince { get; set; }
    [JsonPropertyName("memory")]
    public long Memory { get; set; }
}

internal class RabbitMqMessageStats
{
    [JsonPropertyName("publish")]
    public long Publish { get; set; }
    [JsonPropertyName("deliver_get")]
    public long DeliverGet { get; set; }
    [JsonPropertyName("ack")]
    public long Ack { get; set; }
    [JsonPropertyName("publish_details")]
    public RabbitMqRateDetails? PublishDetails { get; set; }
    [JsonPropertyName("deliver_get_details")]
    public RabbitMqRateDetails? DeliverGetDetails { get; set; }
    [JsonPropertyName("ack_details")]
    public RabbitMqRateDetails? AckDetails { get; set; }
}

internal class RabbitMqRateDetails
{
    [JsonPropertyName("rate")]
    public double Rate { get; set; }
}
