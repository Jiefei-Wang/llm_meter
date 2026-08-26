using System.Diagnostics;

namespace LLMMeter.Collection;

/// <summary>
/// Rolling "last N completed requests" TTFT estimator built from histogram
/// _sum/_count deltas between scrapes. A delta of 1 request yields an exact
/// sample; larger deltas contribute a weighted batch average. The result is
/// exact only if the window consists solely of single-request observations.
/// </summary>
public sealed class RollingTtft(int windowSize = 10)
{
    private sealed class Entry(double seconds, double weight)
    {
        public double Seconds = seconds;
        public double Weight = weight;
    }

    private readonly Queue<Entry> _entries = new();
    private readonly int _window = Math.Max(1, windowSize);
    private bool _sawDataSinceReset;
    private long _lastCount = -1;
    private double _lastSum;
    private long _ticksAtLastUpdate;

    public void Observe(long totalCount, double totalSumSeconds, long stopwatchTicks)
    {
        if (totalCount < _lastCount)
        {
            // Histogram reset (server restart).
            Reset();
            _lastCount = totalCount;
            _lastSum = totalSumSeconds;
            return;
        }

        if (_lastCount >= 0 && totalCount > _lastCount)
        {
            long dCount = totalCount - _lastCount;
            double dSum = totalSumSeconds - _lastSum;
            if (dCount > 0 && dSum > 0)
                Add(dCount, dSum);
        }

        _lastCount = totalCount;
        _lastSum = totalSumSeconds;
        _ticksAtLastUpdate = stopwatchTicks;
    }

    internal void Add(double deltaCount, double deltaSumSeconds)
    {
        _sawDataSinceReset = true;
        Push(deltaSumSeconds / deltaCount, deltaCount);
    }

    /// <summary>Push an individually observed TTFT (exact sample).</summary>
    public void AddExact(double seconds) => Add(1, seconds);

    private void Push(double seconds, double weight)
    {
        _entries.Enqueue(new Entry(seconds, weight));
        Trim();
    }

    private void Trim()
    {
        double total = _entries.Sum(e => e.Weight);
        while (_entries.Count > 1 && total - _entries.Peek().Weight >= _window)
            total -= _entries.Dequeue().Weight;

        // A window partially filled with one huge batch entry can still exceed
        // the nominal size; keep at least that entry for continuity.
        if (_entries.Count > _window)
        {
            // drop oldest whole entries until within window
            while (_entries.Count > _window)
                _entries.Dequeue();
        }
    }

    /// <summary>Weighted mean TTFT in seconds, or null when nothing observed yet.</summary>
    public double? AverageSeconds()
    {
        double wSum = 0, vSum = 0;
        foreach (var e in _entries) { wSum += e.Weight; vSum += e.Seconds * e.Weight; }
        return wSum > 0 ? vSum / wSum : null;
    }

    public int TotalSamples => (int)_entries.Sum(e => e.Weight);

    /// <summary>
    /// True when every contributing observation came from a single-request
    /// delta and enough requests were seen to fill the window.
    /// </summary>
    public bool IsExactEstimate()
    {
        if (!_sawDataSinceReset || _entries.Count == 0) return false;
        foreach (var e in _entries)
            if (e.Weight != 1) return false;
        double total = _entries.Sum(e => e.Weight);
        return total >= _window;
    }

    public int SampleCount => _entries.Count;

    public void Reset()
    {
        _entries.Clear();
        _sawDataSinceReset = false;
        _lastCount = -1;
        _lastSum = 0;
    }

    [Conditional("DEBUG")]
    internal void DebugValidate() { }
}
