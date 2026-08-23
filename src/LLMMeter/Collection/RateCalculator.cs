using System.Diagnostics;
using LLMMeter.Core;

namespace LLMMeter.Collection;

/// <summary>
/// Converts a monotonically increasing counter into a smooth tok/s rate using a
/// rolling time window. The displayed value is the average rate over the last
/// <c>windowSeconds</c> (default 2 s), which keeps the UI stable. When the counter
/// has been flat (displayed 0) and a fresh non-zero interval arrives, the stale
/// window is dropped so the new rate is shown immediately instead of being
/// averaged down. Counter resets (server restart) never produce a negative rate.
/// </summary>
public sealed class RateCalculator(double windowSeconds = 2.0)
{
    private readonly double _windowSeconds = Math.Clamp(windowSeconds, 0.5, 30.0);
    private readonly Queue<(long Ticks, double Counter)> _window = new();

    private double? _lastCounter;
    private long _lastTicks;            // Stopwatch ticks (monotonic)
    private bool _hasDisplay;
    private double _display;

    public const double DefaultWindowSeconds = 2.0;

    /// <summary>Seconds spanned by the rolling window of the last sample (diagnostics).</summary>
    public double LastDtSeconds { get; private set; }

    /// <param name="counter">Cumulative counter value.</param>
    /// <param name="stopwatchTicks">Monotonic clock reading for this sample.</param>
    public MetricValue<double> Update(double counter, long stopwatchTicks)
    {
        // Counter reset (server restart). Discard the window, re-baseline.
        if (_lastCounter.HasValue && counter < _lastCounter.Value - 1e-9)
        {
            _window.Clear();
            _window.Enqueue((stopwatchTicks, counter));
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            _hasDisplay = false;
            _display = 0;
            return MetricValue<double>.Approx(0, MetricSource.Derived, "counter reset; baseline reset");
        }

        if (_lastCounter is null)
        {
            _window.Enqueue((stopwatchTicks, counter));
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            return MetricValue<double>.None; // no interval yet — never invent a number
        }

        double delta = counter - _lastCounter.Value;
        bool nonzero = delta > 1e-9;
        bool wasZero = _hasDisplay && _display <= 1e-9;

        _window.Enqueue((stopwatchTicks, counter));

        // Drop samples older than the window, always keeping at least two.
        long cutoff = stopwatchTicks - (long)(_windowSeconds * Stopwatch.Frequency);
        while (_window.Count > 2 && _window.Peek().Ticks < cutoff)
            _window.Dequeue();

        // Idle → non-zero: drop the stale flat samples so the fresh rate shows
        // immediately instead of being averaged down by the idle window.
        if (wasZero && nonzero)
        {
            while (_window.Count > 2) _window.Dequeue();
        }
        else
        {
            // Re-prune after hypothetical reset to keep the window accurate.
            while (_window.Count > 2 && _window.Peek().Ticks < cutoff)
                _window.Dequeue();
        }

        var oldest = _window.Peek();
        double winDt = (double)(stopwatchTicks - oldest.Ticks) / Stopwatch.Frequency;
        double winDelta = counter - oldest.Counter;

        _lastCounter = counter;
        _lastTicks = stopwatchTicks;
        LastDtSeconds = winDt;

        string note = $"avg over {winDt:0.#}s";

        if (winDt <= 0.0005)
        {
            // Too soon to compute — hold any prior value, else stay silent.
            return _hasDisplay
                ? MetricValue<double>.Approx(_display, MetricSource.Derived, note)
                : MetricValue<double>.None;
        }

        if (winDelta <= 1e-9)
        {
            // Nothing progressed across the whole window: real zero.
            _display = 0;
            _hasDisplay = true;
            return MetricValue<double>.Approx(0, MetricSource.Derived, note);
        }

        double rate = winDelta / winDt;
        _display = rate;
        _hasDisplay = true;
        return MetricValue<double>.Approx(rate, MetricSource.Derived, note);
    }

    /// <summary>Call when the backend identity changed or state was lost.</summary>
    public void Reset()
    {
        _window.Clear();
        _lastCounter = null;
        _lastTicks = 0;
        _hasDisplay = false;
        _display = 0;
    }

    internal static readonly long StopwatchTicksPerSecond = System.Diagnostics.Stopwatch.Frequency;

    public bool HasBaseline => _lastCounter.HasValue;
}
