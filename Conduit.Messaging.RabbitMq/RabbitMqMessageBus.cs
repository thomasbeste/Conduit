using System.Collections.Concurrent;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of IMessageBus.
/// Manages connection, publisher, and consumer channels.
/// </summary>
public sealed class RabbitMqMessageBus(
    RabbitMqSettings settings,
    string serviceName,
    List<ConsumerRegistration> consumerRegistrations,
    IServiceProvider serviceProvider,
    ILogger<RabbitMqMessageBus> logger)
    : IMessageBus, IAsyncDisposable
{
    private IConnection? _connection;
    private RabbitMqPublisher? _publisher;
    private readonly ConcurrentBag<RabbitMqConsumerHost> _consumerHosts = [];
    private bool _started;

    /// <summary>
    /// Optional callback invoked after a message is successfully consumed.
    /// Used by test infrastructure to observe message consumption.
    /// </summary>
    public Action<object, Type>? OnMessageConsumed { get; set; }

    public IMessagePublisher Publisher => _publisher
                                          ?? throw new InvalidOperationException("Message bus has not been started. Call StartAsync first.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;

        logger.LogInformation(
            "Starting RabbitMQ message bus for {ServiceName} on {Host}:{Port}/{VHost}",
            serviceName, settings.Host, settings.Port, settings.VirtualHost);

        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.Username,
            Password = settings.Password,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
            ClientProvidedName = serviceName
        };

        if (settings.UseSsl)
        {
            factory.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = settings.Host,
                Version = SslProtocols.Tls12 | SslProtocols.Tls13
            };
        }

        _connection = await factory.CreateConnectionAsync(cancellationToken);
        logger.LogInformation("RabbitMQ connection established for {ServiceName}", serviceName);

        // Create publisher channel
        var publishChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        _publisher = new RabbitMqPublisher(publishChannel, logger);

        // Create consumer hosts
        foreach (var reg in consumerRegistrations)
        {
            var consumerChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await consumerChannel.BasicQosAsync(0, settings.PrefetchCount, false, cancellationToken);

            var host = new RabbitMqConsumerHost(
                consumerChannel,
                reg,
                serviceName,
                settings,
                serviceProvider,
                logger,
                () => OnMessageConsumed);

            await host.StartAsync(cancellationToken);
            _consumerHosts.Add(host);
        }

        _started = true;

        logger.LogInformation(
            "RabbitMQ message bus started for {ServiceName}: {ConsumerCount} consumers registered",
            serviceName, consumerRegistrations.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;

        logger.LogInformation("Stopping RabbitMQ message bus for {ServiceName}", serviceName);

        foreach (var host in _consumerHosts)
        {
            await host.StopAsync(cancellationToken);
        }

        if (_publisher != null)
        {
            await _publisher.DisposeAsync();
        }

        if (_connection != null)
        {
            await _connection.CloseAsync(cancellationToken);
            await _connection.DisposeAsync();
        }

        _started = false;
        logger.LogInformation("RabbitMQ message bus stopped for {ServiceName}", serviceName);
    }

    public MessageBusHealth GetHealth()
    {
        var isHealthy = _connection is { IsOpen: true } && _started;
        return new MessageBusHealth
        {
            IsHealthy = isHealthy,
            Status = isHealthy ? "Connected" : "Disconnected",
            Details = new MessageBusHealthDetails
            {
                Service = serviceName,
                Host = settings.Host,
                Port = settings.Port,
                VirtualHost = settings.VirtualHost,
                ConsumerCount = consumerRegistrations.Count,
                Started = _started
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
