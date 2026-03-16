using System.Collections.Concurrent;
using System.Security.Authentication;
using Conduit.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of IMessageBus.
/// Manages connection, publisher, and consumer channels.
/// </summary>
public sealed class RabbitMqMessageBus : IMessageBus, IAsyncDisposable
{
    private readonly RabbitMqSettings _settings;
    private readonly string _serviceName;
    private readonly List<ConsumerRegistration> _consumerRegistrations;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RabbitMqMessageBus> _logger;

    private IConnection? _connection;
    private RabbitMqPublisher? _publisher;
    private readonly ConcurrentBag<RabbitMqConsumerHost> _consumerHosts = [];
    private bool _started;

    public RabbitMqMessageBus(
        RabbitMqSettings settings,
        string serviceName,
        List<ConsumerRegistration> consumerRegistrations,
        IServiceProvider serviceProvider,
        ILogger<RabbitMqMessageBus> logger)
    {
        _settings = settings;
        _serviceName = serviceName;
        _consumerRegistrations = consumerRegistrations;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public IMessagePublisher Publisher => _publisher
        ?? throw new InvalidOperationException("Message bus has not been started. Call StartAsync first.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;

        _logger.LogInformation(
            "Starting RabbitMQ message bus for {ServiceName} on {Host}:{Port}/{VHost}",
            _serviceName, _settings.Host, _settings.Port, _settings.VirtualHost);

        var factory = new ConnectionFactory
        {
            HostName = _settings.Host,
            Port = _settings.Port,
            VirtualHost = _settings.VirtualHost,
            UserName = _settings.Username,
            Password = _settings.Password,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
            ClientProvidedName = _serviceName
        };

        if (_settings.UseSsl)
        {
            factory.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = _settings.Host,
                Version = SslProtocols.Tls12 | SslProtocols.Tls13
            };
        }

        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _logger.LogInformation("RabbitMQ connection established for {ServiceName}", _serviceName);

        // Create publisher channel
        var publishChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        _publisher = new RabbitMqPublisher(publishChannel, _serviceProvider, _logger);

        // Create consumer hosts
        foreach (var reg in _consumerRegistrations)
        {
            var consumerChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await consumerChannel.BasicQosAsync(0, _settings.PrefetchCount, false, cancellationToken);

            var host = new RabbitMqConsumerHost(
                consumerChannel,
                reg,
                _serviceName,
                _settings,
                _serviceProvider,
                _logger);

            await host.StartAsync(cancellationToken);
            _consumerHosts.Add(host);
        }

        _started = true;

        _logger.LogInformation(
            "RabbitMQ message bus started for {ServiceName}: {ConsumerCount} consumers registered",
            _serviceName, _consumerRegistrations.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;

        _logger.LogInformation("Stopping RabbitMQ message bus for {ServiceName}", _serviceName);

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
        _logger.LogInformation("RabbitMQ message bus stopped for {ServiceName}", _serviceName);
    }

    public MessageBusHealth GetHealth()
    {
        var isHealthy = _connection is { IsOpen: true } && _started;
        return new MessageBusHealth
        {
            IsHealthy = isHealthy,
            Status = isHealthy ? "Connected" : "Disconnected",
            Details = new Dictionary<string, object>
            {
                ["service"] = _serviceName,
                ["host"] = _settings.Host,
                ["port"] = _settings.Port,
                ["virtualHost"] = _settings.VirtualHost,
                ["consumerCount"] = _consumerRegistrations.Count,
                ["started"] = _started
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>
/// Registration of a consumer type and its message type.
/// </summary>
public sealed class ConsumerRegistration
{
    public required Type ConsumerType { get; init; }
    public required Type MessageType { get; init; }
}
