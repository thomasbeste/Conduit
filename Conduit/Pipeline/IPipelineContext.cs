namespace Conduit;

/// <summary>
/// Optional pipeline context that accumulates data across multiple Send() calls.
/// Inject as IPipelineContext? and check for null before using.
/// </summary>
public interface IPipelineContext
{
    /// <summary>
    /// Starts a timer with the given name. Dispose the returned scope to stop and record the timing.
    /// </summary>
    ITimerScope StartTimer(string name);

    /// <summary>
    /// Gets all recorded timings.
    /// </summary>
    IReadOnlyList<TimingEntry> GetTimings();

    /// <summary>
    /// Increments a counter metric by the specified value.
    /// </summary>
    void Increment(string name, long value = 1);

    /// <summary>
    /// Records a value for a metric (updates count, total, min, max).
    /// </summary>
    void Record(string name, double value);

    /// <summary>
    /// Gets all recorded metrics.
    /// </summary>
    IReadOnlyDictionary<string, MetricEntry> GetMetrics();

    /// <summary>
    /// Arbitrary data bag for storing custom data across the pipeline.
    /// </summary>
    IDictionary<string, object?> Items { get; }
}

/// <summary>
/// A timer scope that records elapsed time when disposed.
/// </summary>
public interface ITimerScope : IDisposable
{
    /// <summary>
    /// The name of this timer.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The elapsed time since the timer was started.
    /// </summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// Stops the timer and records the timing. Called automatically on Dispose.
    /// </summary>
    void Stop();
}

/// <summary>
/// Represents a recorded timing entry.
/// </summary>
public record TimingEntry(string Name, TimeSpan Elapsed, DateTimeOffset StartedAt);

/// <summary>
/// Represents a recorded metric entry with aggregated statistics.
/// </summary>
public record MetricEntry(string Name, long Count, double Total, double Min, double Max)
{
    public double Average => Count > 0 ? Total / Count : 0;
}
