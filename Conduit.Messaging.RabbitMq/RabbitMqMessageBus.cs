using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of IMessageBus.
///
/// Owns ONE long-lived <see cref="IConnection"/> shared by the publisher and all
/// consumer hosts, plus a transport-agnostic <see cref="ConsumerSupervisor"/>
/// that keeps every consumer alive. The connection is resolved through
/// <see cref="GetConnectionAsync"/>, which recreates it when it has permanently
/// died — so neither a dead channel nor a dead connection can strand a consumer
/// or publisher. (The old design relied on the client library's best-effort
/// automatic recovery and never recreated a connection whose recovery thread had
/// given up — the 2026-06-02 stranded-consumer incident.)
/// </summary>
public sealed class RabbitMqMessageBus(
    RabbitMqSettings settings,
    string serviceName,
    List<ConsumerRegistration> consumerRegistrations,
    IServiceProvider serviceProvider,
    ILogger<RabbitMqMessageBus> logger)
    : IMessageBus, IAsyncDisposable
{
    private ConnectionFactory? _factory;
    private IConnection? _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private RabbitMqPublisher? _publisher;
    private readonly List<RabbitMqConsumerHost> _consumerHosts = [];
    private ConsumerSupervisor? _supervisor;
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

        // Publisher + consumer hosts resolve the connection through
        // GetConnectionAsync; none holds a fixed reference, so a broker bounce
        // (or a permanently-dead connection) is recovered centrally.
        _publisher = new RabbitMqPublisher(GetConnectionAsync, logger);

        foreach (var reg in consumerRegistrations)
        {
            _consumerHosts.Add(new RabbitMqConsumerHost(
                GetConnectionAsync,
                reg,
                serviceName,
                settings,
                serviceProvider,
                logger,
                () => OnMessageConsumed));
        }

        // The supervisor brings every consumer up and keeps it up — forever,
        // through any outage. If the broker is down right now, StartAsync still
        // returns; the supervisor retries until it's reachable.
        _supervisor = new ConsumerSupervisor(_consumerHosts, logger);
        await _supervisor.StartAsync(cancellationToken);

        _started = true;

        logger.LogInformation(
            "RabbitMQ message bus started for {ServiceName}: {ConsumerCount} consumers under supervision",
            serviceName, consumerRegistrations.Count);
    }

    /// <summary>
    /// Returns a live connection, recreating it under a lock if the current one
    /// is null or has died. Called by the publisher and every consumer host
    /// whenever they need to (re)open a channel — this is the single place a
    /// permanently-dead connection gets replaced.
    /// </summary>
    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        var existing = _connection;
        if (existing is { IsOpen: true }) return existing;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;

            if (_connection is not null)
            {
                try { await _connection.DisposeAsync(); } catch { /* already dead */ }
                _connection = null;
            }

            _factory ??= BuildFactory();

            // Bounded connect attempt: fail fast when the broker is down so the
            // caller (a supervisor probe) backs off and retries, rather than
            // hanging a rebuild for minutes.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(15));
            _connection = await _factory.CreateConnectionAsync(linkedCts.Token);
            logger.LogInformation("RabbitMQ connection established for {ServiceName}", serviceName);
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private ConnectionFactory BuildFactory()
    {
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.Username,
            Password = settings.Password,
            // Keep the library's automatic recovery on as a fast-path for brief
            // blips, but it is no longer the durability mechanism — the
            // ConsumerSupervisor + GetConnectionAsync own that and don't depend
            // on any recovery event firing.
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
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

        return factory;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;

        logger.LogInformation("Stopping RabbitMQ message bus for {ServiceName}", serviceName);

        if (_supervisor is not null)
        {
            try { await _supervisor.DisposeAsync(); } catch (ObjectDisposedException) { }
        }

        foreach (var host in _consumerHosts)
        {
            try { await host.DisposeAsync(); } catch (ObjectDisposedException) { }
        }

        if (_publisher != null)
        {
            try { await _publisher.DisposeAsync(); } catch (ObjectDisposedException) { }
        }

        if (_connection != null)
        {
            try { await _connection.CloseAsync(cancellationToken); }
            catch (ObjectDisposedException) { }
            catch (RabbitMQ.Client.Exceptions.AlreadyClosedException) { }
            finally { await _connection.DisposeAsync(); }
        }

        _connectionLock.Dispose();
        _started = false;
        logger.LogInformation("RabbitMQ message bus stopped for {ServiceName}", serviceName);
    }

    public MessageBusHealth GetHealth()
    {
        // Healthy iff started and every supervised consumer is currently
        // consuming. A publisher-only service (no consumers) is healthy on the
        // connection alone. The supervisor keeps consumers healthy within a
        // probe interval, so a transient false here self-corrects.
        var consumersHealthy = _consumerHosts.Count == 0
            ? _connection is { IsOpen: true }
            : _consumerHosts.TrueForAll(h => h.IsHealthy);
        var isHealthy = _started && consumersHealthy;

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
