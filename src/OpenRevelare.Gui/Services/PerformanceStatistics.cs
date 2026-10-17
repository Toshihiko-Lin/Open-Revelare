namespace OpenRevelare.Gui.Services;

/// <summary>Bounded per-phase samples. Percentiles describe the most recent samples, while
/// count/total/max describe the session; nested and parallel durations must not be summed.</summary>
internal sealed class PerformanceStatistics
{
    private sealed class Samples
    {
        internal long Count;
        internal double Total, Max;
        internal readonly Queue<double> Recent = new();
    }
    internal sealed record Summary(long Count, int SampleCount, double TotalMs,
        double MaximumMs, double P50Ms, double P95Ms);
    private readonly object _gate = new();
    private readonly Dictionary<string, Samples> _phases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly int _capacity;

    internal PerformanceStatistics(int capacity = 2048)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal void Observe(string phase, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            if (!_phases.TryGetValue(phase, out var samples)) _phases[phase] = samples = new();
            samples.Count++; samples.Total += milliseconds; samples.Max = Math.Max(samples.Max, milliseconds);
            if (samples.Recent.Count == _capacity) samples.Recent.Dequeue();
            samples.Recent.Enqueue(milliseconds);
        }
    }

    internal void Increment(string counter)
    {
        lock (_gate) _counters[counter] = _counters.GetValueOrDefault(counter) + 1;
    }

    internal Dictionary<string, long> Counters() { lock (_gate) return new(_counters); }

    internal Dictionary<string, Summary> Snapshot()
    {
        lock (_gate)
            return _phases.ToDictionary(p => p.Key, p =>
            {
                var s = p.Value;
                double[] sorted = s.Recent.Order().ToArray();
                double Percentile(double quantile) => sorted[Math.Max(0, (int)Math.Ceiling(quantile * sorted.Length) - 1)];
                return new Summary(s.Count, sorted.Length, s.Total, s.Max, Percentile(0.5), Percentile(0.95));
            });
    }
}
