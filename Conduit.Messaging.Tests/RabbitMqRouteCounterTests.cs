using System.Net;
using System.Text;
using Conduit.Messaging.RabbitMq;

namespace Conduit.Messaging.Tests;

public class RabbitMqRouteCounterTests
{
    [Theory]
    [InlineData("case.*.ready", "case.42.ready", true)]
    [InlineData("case.#", "case.42.document.ready", true)]
    [InlineData("#.ready", "case.42.ready", true)]
    [InlineData("case.*.ready", "case.42.document.ready", false)]
    [InlineData("case.ready", "case.42.ready", false)]
    public void Topic_matching_follows_amqp_wildcards(string pattern, string route, bool expected)
    {
        Assert.Equal(expected, RabbitMqRouteCounter.TopicMatches(pattern, route));
    }

    [Fact]
    public async Task Route_count_deduplicates_queue_bindings_and_filters_topic_routes()
    {
        const string json = """
            [
              {"destination":"queue-a","destination_type":"queue","routing_key":"case.*"},
              {"destination":"queue-a","destination_type":"queue","routing_key":"case.#"},
              {"destination":"queue-b","destination_type":"queue","routing_key":"other.#"}
            ]
            """;
        var client = new HttpClient(new StaticResponseHandler(json));
        var counter = new RabbitMqRouteCounter(
            new RabbitMqSettings { Host = "rabbitmq", VirtualHost = "gpi" }, client);

        var count = await counter.CountAsync(
            "events", "topic", "case.42", TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Route_count_uses_configured_management_url()
    {
        var handler = new StaticResponseHandler("""
            [{"destination":"queue-a","destination_type":"queue","routing_key":""}]
            """);
        var counter = new RabbitMqRouteCounter(
            new RabbitMqSettings
            {
                Host = "amqp.example.test",
                VirtualHost = "gpi",
                ManagementUrl = "https://management.example.test/rabbit/",
            },
            new HttpClient(handler));

        var count = await counter.CountAsync(
            "events", "fanout", "", TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal(
            "https://management.example.test/rabbit/api/exchanges/gpi/events/bindings/source",
            handler.LastRequestUri?.ToString());
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
