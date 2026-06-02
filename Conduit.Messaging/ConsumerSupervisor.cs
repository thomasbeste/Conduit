using Microsoft.Extensions.Logging;

namespace Conduit.Messaging;

/// <summary>
/// Transport-agnostic watchdog that keeps a set of <see cref="ISupervisedConsumer"/>
/// instances consuming, for the lifetime of the process, regardless of how long
/// or how violently the broker is unavailable.
///
/// This is the load-bearing piece that makes durability a GUARANTEE of the
/// messaging abstraction rather than an accident of whichever client library a
/// provider happens to wrap. The loop only ever asks two transport-agnostic
/// questions — "are you healthy?" and, if not, "rebuild yourself" — so the same
/// supervisor drives RabbitMQ, Azure Service Bus, or any future transport.
///
/// Design invariants:
///   * It never gives up. There is no attempt cap; a consumer that can't be
///     rebuilt is simply retried on the next probe tick, forever.
///   * It never throws out of the loop. A rebuild failure (broker still down) is
///     logged and retried next tick — one sick consumer can't kill the watchdog.
///   * It doesn't trust client-library recovery events. Liveness is polled, so a
///     consumer that died silently (no event fired, recovery thread gave up) is
///     still detected and healed.
/// </summary>
public sealed class ConsumerSupervisor(
    IReadOnlyList<ISupervisedConsumer> consumers,
    ILogger logger,
    TimeSpan? probeInterval = null) : IAsyncDisposable
{
    private readonly TimeSpan _probeInterval = probeInterval ?? TimeSpan.FromSeconds(15);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    // Names of consumers we've already logged as down, so a persistent fault
    // (broker still gone, incompatible queue args) is announced once at Warning
    // and then kept at Debug — not re-spammed every probe. Cleared on recovery.
    // Single-threaded: only the supervise loop touches it.
    private readonly HashSet<string> _loggedDown = [];

    /// <summary>
    /// Bring every consumer up (best-effort), then start the background watchdog.
    /// The passed token bounds only the initial bring-up; the watchdog itself
    /// runs until <see cref="DisposeAsync"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null) return;

        // Initial bring-up. Failures here are non-fatal — the watchdog will keep
        // retrying — so a broker that's slow to come up never blocks startup.
        foreach (var consumer in consumers)
        {
            try
            {
                await consumer.EnsureRunningAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Consumer {Name} did not start on first attempt; supervisor will keep retrying", consumer.Name);
            }
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => SuperviseLoopAsync(_cts.Token));

        logger.LogInformation(
            "Consumer supervisor watching {Count} consumer(s), probe every {Interval}s",
            consumers.Count, _probeInterval.TotalSeconds);
    }

    private async Task SuperviseLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_probeInterval, ct); }
            catch (OperationCanceledException) { return; }

            foreach (var consumer in consumers)
            {
                if (ct.IsCancellationRequested) return;

                bool healthy;
                try { healthy = consumer.IsHealthy; }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Health probe threw for {Name}; treating as unhealthy", consumer.Name);
                    healthy = false;
                }

                if (healthy) continue;

                // Announce the down-state once at Warning; while it persists,
                // drop to Debug so a long outage doesn't spam every probe.
                var firstTimeDown = _loggedDown.Add(consumer.Name);
                if (firstTimeDown)
                    logger.LogWarning("Consumer {Name} is not consuming — rebuilding", consumer.Name);
                else
                    logger.LogDebug("Consumer {Name} still not consuming — retrying rebuild", consumer.Name);

                try
                {
                    await consumer.EnsureRunningAsync(ct);
                    if (consumer.IsHealthy && _loggedDown.Remove(consumer.Name))
                        logger.LogInformation("Consumer {Name} rebuilt and consuming again", consumer.Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Broker still unreachable, or rebuild raced another failure.
                    // Don't escalate — next tick tries again. Forever. Loud on the
                    // first failure, Debug thereafter so a persistent fault (broker
                    // down, incompatible queue args) doesn't flood the log.
                    if (firstTimeDown)
                        logger.LogWarning(ex,
                            "Consumer {Name} rebuild failed; will retry every {Interval}s until it recovers",
                            consumer.Name, _probeInterval.TotalSeconds);
                    else
                        logger.LogDebug(ex,
                            "Consumer {Name} rebuild still failing (every {Interval}s)",
                            consumer.Name, _probeInterval.TotalSeconds);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { /* expected */ }
        }

        _cts?.Dispose();
    }
}
