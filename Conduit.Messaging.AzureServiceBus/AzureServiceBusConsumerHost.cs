using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Conduit.Messaging.AzureServiceBus;

/// <summary>
/// Adapts an Azure Service Bus <see cref="ServiceBusProcessor"/> to
/// <see cref="ISupervisedConsumer"/> so the SAME transport-agnostic
/// <see cref="ConsumerSupervisor"/> governs it.
///
/// The SDK's processor already self-heals across broker disconnects (it owns the
/// AMQP link and reconnects indefinitely) — which is why ASB never had the
/// stranded-consumer bug the hand-rolled RabbitMQ path did. So
/// <see cref="EnsureRunningAsync"/> is normally a no-op. Routing ASB through the
/// same supervisor anyway is the point: durability becomes a uniform GUARANTEE
/// of the abstraction, proven the same way for every transport, instead of an
/// accident of which client library a provider happens to wrap. It also covers
/// the one case the SDK won't recover on its own — a processor that has been
/// stopped/faulted into a closed state — by recreating it from the factory.
/// </summary>
internal sealed class AzureServiceBusConsumerHost(
    string name,
    Func<ServiceBusProcessor> processorFactory,
    ILogger logger) : ISupervisedConsumer
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ServiceBusProcessor? _processor;
    private volatile bool _disposed;

    public string Name => name;

    public bool IsHealthy => !_disposed && _processor is { IsClosed: false, IsProcessing: true };

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsHealthy) return;

            if (_processor is null || _processor.IsClosed)
            {
                if (_processor is not null)
                {
                    try { await _processor.DisposeAsync(); } catch { /* already dead */ }
                }
                _processor = processorFactory();
            }

            if (!_processor.IsProcessing)
            {
                await _processor.StartProcessingAsync(cancellationToken);
                logger.LogInformation("ASB processor {Name} started processing", name);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lock.WaitAsync();
        try
        {
            if (_processor is not null)
            {
                try
                {
                    if (_processor.IsProcessing)
                        await _processor.StopProcessingAsync();
                }
                catch { /* best-effort */ }
                await _processor.DisposeAsync();
                _processor = null;
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
