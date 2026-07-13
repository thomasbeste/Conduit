using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// Counts distinct queues currently bound to an exchange through RabbitMQ's
/// management API. Claim-check publishers use this once per route so a shared
/// payload survives until every fanout/topic queue has settled successfully.
/// </summary>
internal sealed class RabbitMqRouteCounter
{
    private readonly RabbitMqSettings _settings;
    private readonly HttpClient _client;

    public RabbitMqRouteCounter(RabbitMqSettings settings)
        : this(settings, BuildClient(settings))
    {
    }

    internal RabbitMqRouteCounter(RabbitMqSettings settings, HttpClient client)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<int> CountAsync(
        string exchangeName,
        string exchangeType,
        string routingKey,
        CancellationToken cancellationToken)
    {
        var vhost = Uri.EscapeDataString(_settings.VirtualHost);
        var exchange = Uri.EscapeDataString(exchangeName);
        var defaultScheme = _settings.UseSsl ? "https" : "http";
        var defaultPort = _settings.UseSsl ? 15671 : 15672;
        var managementBase = _settings.ManagementUrl?.TrimEnd('/')
            ?? $"{defaultScheme}://{_settings.Host}:{defaultPort}";
        var url = $"{managementBase}/api/exchanges/{vhost}/{exchange}/bindings/source";
        using var response = await _client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var bindings = JsonSerializer.Deserialize<List<Binding>>(json) ?? [];
        var routedBindings = bindings
            .Where(binding => exchangeType == "fanout" || TopicMatches(binding.RoutingKey, routingKey))
            .ToList();
        if (routedBindings.Any(binding =>
                string.Equals(binding.DestinationType, "exchange", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Cannot safely count claim-check owners for exchange '{exchangeName}': " +
                "the route contains an exchange-to-exchange binding.");
        }

        var count = routedBindings
            .Where(binding => string.Equals(binding.DestinationType, "queue", StringComparison.Ordinal))
            .Select(binding => binding.Destination)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (count < 1)
        {
            throw new InvalidOperationException(
                $"Refusing to offload claim-check payload for exchange '{exchangeName}': " +
                "RabbitMQ reports no routed queue owners.");
        }

        return count;
    }

    internal static bool TopicMatches(string pattern, string routingKey)
    {
        var patternParts = pattern.Split('.', StringSplitOptions.None);
        var routingParts = routingKey.Split('.', StringSplitOptions.None);
        return Match(patternParts, 0, routingParts, 0);
    }

    private static bool Match(string[] pattern, int p, string[] routing, int r)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "#")
            {
                if (p == pattern.Length - 1)
                    return true;
                for (var next = r; next <= routing.Length; next++)
                {
                    if (Match(pattern, p + 1, routing, next))
                        return true;
                }
                return false;
            }

            if (r >= routing.Length || (pattern[p] != "*" && pattern[p] != routing[r]))
                return false;

            p++;
            r++;
        }

        return r == routing.Length;
    }

    private static HttpClient BuildClient(RabbitMqSettings settings)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var auth = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{settings.Username}:{settings.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        return client;
    }

    private sealed class Binding
    {
        [JsonPropertyName("destination")]
        public string Destination { get; init; } = string.Empty;

        [JsonPropertyName("destination_type")]
        public string DestinationType { get; init; } = string.Empty;

        [JsonPropertyName("routing_key")]
        public string RoutingKey { get; init; } = string.Empty;
    }
}
