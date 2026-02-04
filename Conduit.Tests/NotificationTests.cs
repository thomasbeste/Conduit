using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class NotificationTests
{
    public record UserCreated(string Username) : INotification;

    public class EmailHandler : INotificationHandler<UserCreated>
    {
        public static List<string> SentEmails { get; } = [];

        public Task Handle(UserCreated notification, CancellationToken cancellationToken)
        {
            SentEmails.Add($"Email to {notification.Username}");
            return Task.CompletedTask;
        }
    }

    public class LoggingHandler : INotificationHandler<UserCreated>
    {
        public static List<string> Logs { get; } = [];

        public Task Handle(UserCreated notification, CancellationToken cancellationToken)
        {
            Logs.Add($"User created: {notification.Username}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Publish_NotifiesAllHandlers()
    {
        EmailHandler.SentEmails.Clear();
        LoggingHandler.Logs.Clear();

        var services = new ServiceCollection();
        services.AddConduit(cfg => cfg.RegisterServicesFromAssemblyContaining<NotificationTests>());

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.Publish(new UserCreated("jin_yang"));

        Assert.Single(EmailHandler.SentEmails);
        Assert.Contains("jin_yang", EmailHandler.SentEmails[0]);
        Assert.Single(LoggingHandler.Logs);
        Assert.Contains("jin_yang", LoggingHandler.Logs[0]);
    }

    [Fact]
    public async Task Publish_UntypedNotification_Works()
    {
        EmailHandler.SentEmails.Clear();
        LoggingHandler.Logs.Clear();

        var services = new ServiceCollection();
        services.AddConduit(cfg => cfg.RegisterServicesFromAssemblyContaining<NotificationTests>());

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        object notification = new UserCreated("erlich");
        await dispatcher.Publish(notification);

        Assert.Single(EmailHandler.SentEmails);
        Assert.Single(LoggingHandler.Logs);
    }

    [Fact]
    public async Task Publish_NoHandlers_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddConduit(_ => { }); // No handlers

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        // Should complete without throwing
        await dispatcher.Publish(new UserCreated("nobody"));
    }

    [Fact]
    public async Task Publish_WithTaskWhenAllPublisher_ExecutesInParallel()
    {
        EmailHandler.SentEmails.Clear();
        LoggingHandler.Logs.Clear();

        var services = new ServiceCollection();
        services.AddConduit(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<NotificationTests>();
            cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher);
        });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.Publish(new UserCreated("parallel_jian"));

        Assert.Single(EmailHandler.SentEmails);
        Assert.Single(LoggingHandler.Logs);
    }
}
