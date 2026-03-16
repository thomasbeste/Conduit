using System.Diagnostics;

namespace Conduit.Mediator;

/// <summary>
/// Thread-safe implementation of <see cref="IPipelineContext"/> that accumulates data across pipeline executions.
/// </summary>
/// <remarks>
/// This class is registered as a scoped service, meaning one instance per DI scope (typically per HTTP request).
/// All public methods are thread-safe for concurrent access from parallel pipeline executions.
/// Also supports ambient (AsyncLocal) access via <see cref="Current"/> for cross-cutting concerns.
/// </remarks>
public sealed class PipelineContext : IPipelineContext
{
    private static readonly AsyncLocal<PipelineContext?> _current = new();

    /// <summary>
    /// Gets the ambient pipeline context for the current async flow.
    /// Returns null if no context has been established.
    /// </summary>
    public static PipelineContext? Current => _current.Value;

    /// <summary>
    /// Sets the ambient pipeline context for the current async flow.
    /// Returns an IDisposable that restores the previous context when disposed.
    /// </summary>
    public static IDisposable SetCurrent(PipelineContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new ContextRestorer(previous);
    }

    private sealed class ContextRestorer(PipelineContext? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }

    private readonly List<TimingEntry> _timings = [];
    private readonly Dictionary<string, MetricEntry> _metrics = [];
    private readonly Dictionary<string, object?> _items = [];
    private readonly object _lock = new();

    public IDictionary<string, object?> Items => _items;

    public ITimerScope StartTimer(string name)
    {
        return new TimerScope(name, this);
    }

    public IReadOnlyList<TimingEntry> GetTimings()
    {
        lock (_lock)
        {
            return _timings.ToList();
        }
    }

    public void Increment(string name, long value = 1)
    {
        lock (_lock)
        {
            if (_metrics.TryGetValue(name, out var existing))
            {
                _metrics[name] = existing with
                {
                    Count = existing.Count + value,
                    Total = existing.Total + value
                };
            }
            else
            {
                _metrics[name] = new MetricEntry(name, value, value, value, value);
            }
        }
    }

    public void Record(string name, double value)
    {
        lock (_lock)
        {
            if (_metrics.TryGetValue(name, out var existing))
            {
                _metrics[name] = new MetricEntry(
                    name,
                    existing.Count + 1,
                    existing.Total + value,
                    Math.Min(existing.Min, value),
                    Math.Max(existing.Max, value)
                );
            }
            else
            {
                _metrics[name] = new MetricEntry(name, 1, value, value, value);
            }
        }
    }

    public IReadOnlyDictionary<string, MetricEntry> GetMetrics()
    {
        lock (_lock)
        {
            return new Dictionary<string, MetricEntry>(_metrics);
        }
    }

    internal void RecordTiming(TimingEntry entry)
    {
        lock (_lock)
        {
            _timings.Add(entry);
        }
    }

    private sealed class TimerScope(string name, PipelineContext context) : ITimerScope
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private bool _stopped;

        public string Name => name;
        public TimeSpan Elapsed => _sw.Elapsed;

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _sw.Stop();
            context.RecordTiming(new TimingEntry(name, _sw.Elapsed, _startedAt));
        }

        public void Dispose() => Stop();
    }
}
