using Conduit.Messaging.InMemory;
using Conduit.Messaging.Registration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Messaging.Tests;

public class RegistrationTests
{
    private record TestMessage(string Value);

    private class TestConsumer : IMessageConsumer<TestMessage>
    {
        public Task ConsumeAsync(TestMessage message, MessageContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void AddConduitMessaging_registers_IMessageBus_and_IMessagePublisher()
    {
        var services = new ServiceCollection();
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.ServiceName = "test";
        });

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<IMessageBus>());
        Assert.NotNull(sp.GetService<IMessagePublisher>());
    }

    [Fact]
    public void AddConsumer_registers_consumer_type_in_DI()
    {
        var services = new ServiceCollection();
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.ServiceName = "test";
            cfg.AddConsumer<TestConsumer>();
        });

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<TestConsumer>());
    }

    [Fact]
    public void InMemoryMessageBus_is_resolvable_directly()
    {
        var services = new ServiceCollection();
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.ServiceName = "test";
        });

        var sp = services.BuildServiceProvider();
        var inMemory = sp.GetService<InMemoryMessageBus>();
        Assert.NotNull(inMemory);
    }

    [Fact]
    public async Task AddConsumersFromAssembly_finds_and_wires_consumers()
    {
        var services = new ServiceCollection();
        services.AddConduitMessaging(cfg =>
        {
            cfg.UseInMemory();
            cfg.ServiceName = "test";
            cfg.AddConsumersFromAssembly(typeof(TestConsumer).Assembly);
        });

        var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IMessageBus>();
        await bus.StartAsync();

        // If assembly scanning found TestConsumer, publishing should dispatch to it
        await bus.Publisher.PublishAsync(new TestMessage("scanned"));

        var inMemory = sp.GetRequiredService<InMemoryMessageBus>();
        var consumed = inMemory.GetConsumed<TestMessage>().ToList();
        Assert.Contains(consumed, m => m.Value == "scanned");
    }
}
