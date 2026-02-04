using Microsoft.Extensions.DependencyInjection;

namespace Cypher.Tests;

public class RegistrationTests
{
    [Fact]
    public void AddCypher_RegistersIDispatcher()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => { });

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetService<IDispatcher>();

        Assert.NotNull(dispatcher);
        Assert.IsType<Dispatcher>(dispatcher);
    }

    [Fact]
    public void AddCypher_RegistersISender()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => { });

        var provider = services.BuildServiceProvider();
        var sender = provider.GetService<ISender>();

        Assert.NotNull(sender);
    }

    [Fact]
    public void AddCypher_RegistersIPublisher()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => { });

        var provider = services.BuildServiceProvider();
        var publisher = provider.GetService<IPublisher>();

        Assert.NotNull(publisher);
    }

    [Fact]
    public void AddCypher_DefaultsToForeachAwaitPublisher()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => { });

        var provider = services.BuildServiceProvider();
        var publisher = provider.GetService<INotificationPublisher>();

        Assert.NotNull(publisher);
        Assert.IsType<ForeachAwaitPublisher>(publisher);
    }

    [Fact]
    public void AddCypher_CanConfigureTaskWhenAllPublisher()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher));

        var provider = services.BuildServiceProvider();
        var publisher = provider.GetService<INotificationPublisher>();

        Assert.NotNull(publisher);
        Assert.IsType<TaskWhenAllPublisher>(publisher);
    }

    [Fact]
    public void AddCypher_CanConfigureLifetime()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => cfg.Lifetime = ServiceLifetime.Singleton);

        // Verify that the same instance is returned
        var provider = services.BuildServiceProvider();
        var dispatcher1 = provider.GetService<IDispatcher>();
        var dispatcher2 = provider.GetService<IDispatcher>();

        Assert.Same(dispatcher1, dispatcher2);
    }

    [Fact]
    public void AddCypher_ScansAssemblyForHandlers()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => cfg.RegisterServicesFromAssemblyContaining<RegistrationTests>());

        var provider = services.BuildServiceProvider();

        // Should be able to resolve a handler from this assembly
        var handler = provider.GetService<IRequestHandler<BasicRequestTests.Ping, BasicRequestTests.Pong>>();
        Assert.NotNull(handler);
    }

    [Fact]
    public void AddOpenBehavior_ThrowsForNonGenericType()
    {
        var config = new CypherConfiguration();

        Assert.Throws<ArgumentException>(() => config.AddOpenBehavior(typeof(string)));
    }

    #region Validation Tests

    public record OrphanRequest(string Value) : IRequest<string>;
    // No handler for OrphanRequest - intentionally missing for testing

    [Fact]
    public void ValidateCypherRegistrations_PassesWhenNoRequestTypes()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => { });

        var provider = services.BuildServiceProvider();

        // Validate the main library assembly which has no request types - should pass vacuously
        provider.ValidateCypherRegistrations(typeof(IDispatcher).Assembly);
    }

    [Fact]
    public void ValidateCypherRegistrations_ThrowsWhenHandlerMissing()
    {
        var services = new ServiceCollection();
        // Don't register handlers from the test assembly
        services.AddCypher(cfg => { });

        var provider = services.BuildServiceProvider();

        // Should throw because OrphanRequest has no handler
        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.ValidateCypherRegistrations(typeof(OrphanRequest).Assembly));

        Assert.Contains("OrphanRequest", ex.Message);
        Assert.Contains("No handler registered", ex.Message);
    }

    [Fact]
    public void ValidateCypherRegistrations_PassesWhenHandlersRegistered()
    {
        var services = new ServiceCollection();
        // Register handlers from test assembly
        services.AddCypher(cfg => cfg.RegisterServicesFromAssemblyContaining<RegistrationTests>());

        var provider = services.BuildServiceProvider();

        // Should throw only for OrphanRequest (which has no handler)
        // but NOT for Ping, GetValue, etc. (which have handlers)
        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.ValidateCypherRegistrations(typeof(RegistrationTests).Assembly));

        // Should only contain OrphanRequest, not the ones with handlers
        Assert.Contains("OrphanRequest", ex.Message);
        Assert.DoesNotContain("Ping", ex.Message);
        Assert.DoesNotContain("GetValue", ex.Message);
    }

    [Fact]
    public void ValidateCypherRegistrations_ReportsMultipleMissingHandlers()
    {
        var services = new ServiceCollection();
        services.AddCypher(cfg => { }); // No handlers registered

        var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.ValidateCypherRegistrations(typeof(RegistrationTests).Assembly));

        // Should contain errors for multiple request types
        Assert.Contains("No handler registered", ex.Message);
        // Should list multiple errors (more than just OrphanRequest)
        var errorLines = ex.Message.Split('\n').Where(l => l.Contains("No handler")).ToList();
        Assert.True(errorLines.Count > 1, "Expected multiple missing handler errors");
    }

    #endregion
}
