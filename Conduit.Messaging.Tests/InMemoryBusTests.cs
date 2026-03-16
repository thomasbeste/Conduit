using Conduit.Messaging.InMemory;
using Conduit.Messaging.Registration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Messaging.Tests;

public class InMemoryBusTests
{
    private record TestMessage(string Value);

    private class TestConsumer : IMessageConsumer<TestMessage>
    {
        public List<TestMessage> Received { get; } = [];

        public Task ConsumeAsync(TestMessage message, MessageContext context, CancellationToken cancellationToken = default)
        {
            Received.Add(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Published_message_is_dispatched_to_consumer()
    {
        var services = new ServiceCollection();
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.ServiceName = "test";
            cfg.AddConsumer<TestConsumer>();
        });

        var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IMessageBus>();
        await bus.StartAsync();

        var msg = new TestMessage("hello");
        await bus.Publisher.PublishAsync(msg);

        var inMemory = sp.GetRequiredService<InMemoryMessageBus>();
        var published = inMemory.GetPublished<TestMessage>().ToList();
        Assert.Single(published);
        Assert.Equal("hello", published[0].Value);
    }

    [Fact]
    public async Task GetHealth_returns_healthy_when_started()
    {
        var services = new ServiceCollection();
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.ServiceName = "test";
        });

        var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IMessageBus>();

        Assert.False(bus.GetHealth().IsHealthy);

        await bus.StartAsync();
        Assert.True(bus.GetHealth().IsHealthy);

        await bus.StopAsync();
        Assert.False(bus.GetHealth().IsHealthy);
    }

    [Fact]
    public async Task WaitForConsume_returns_matching_message()
    {
        var services = new ServiceCollection();
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.ServiceName = "test";
            cfg.AddConsumer<TestConsumer>();
        });

        var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IMessageBus>();
        await bus.StartAsync();

        var msg = new TestMessage("target");
        await bus.Publisher.PublishAsync(msg);

        var inMemory = sp.GetRequiredService<InMemoryMessageBus>();
        var result = await inMemory.WaitForConsume<TestMessage>(m => m.Value == "target");
        Assert.Equal("target", result.Value);
    }
}
