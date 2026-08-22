using System.Diagnostics;
using LLMMeter.Core;

namespace LLMMeter.Collection;

/// <summary>
/// Converts a monotonically increasing counter into a smoothed tok/s rate.
/// Handles counter resets (never negative), stale counters (displays 0 after
/// ~2 unchanged samples) and uses an EMA to keep the UI calm.
/// </summary>
public sealed class RateCalculator(double emaAlpha = 0.35)
{
    private readonly double _alpha = Math.Clamp(emaAlpha, 0.05, 1.0);
    private double? _lastCounter;
    private long _lastTicks;          // Stopwatch ticks (monotonic)
    private int _staleSamples;
    private bool _hasDisplay;
    private double _display;

    public const double DefaultEmaAlpha = 0.35;

    /// <summary>Seconds since last accepted sample (diagnostics).</summary>
    public double LastDtSeconds { get; private set; }

    /// <param name="counter">Cumulative counter value.</param>
    /// <param name="stopwatchTicks">Monotonic clock reading for this sample.</param>
    public MetricValue<double> Update(double counter, long stopwatchTicks)
    {
        if (_lastCounter.HasValue && counter < _lastCounter.Value - 1e-9)
        {
            // Counter reset (server restart). Discard interval, re-baseline.
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            _staleSamples = 0;
            return MetricValue<double>.Approx(0, MetricSource.Derived, "counter reset; baseline reset");
        }

        if (_lastCounter is null)
        {
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            return MetricValue<double>.None; // no interval yet
        }

        double dt = (double)(stopwatchTicks - _lastTicks) / Stopwatch.Frequency;
        string note = $"counter delta over {dt:0.#}s";
        if (dt <= 0.0005) return CurrentOrNone();
        double delta = counter - _lastCounter.Value;
        double rate = delta / dt;

        if (delta <= 0) _staleSamples++; else _staleSamples = 0;
        if (_staleSamples >= 2)
        {
            // Backend confirmed no token progress — show a real zero.
            _display = 0;
            _hasDisplay = true;
        }
        else if (_staleSamples == 1 && _hasDisplay)
        {
            // Single unchanged sample: hold previous value (no flicker to zero).
        }
        else if (!_hasDisplay)
        {
            _display = rate;
            _hasDisplay = true;
        }
        else
        {
            _display = _alpha * rate + (1 - _alpha) * _display;
        }

        _lastCounter = counter;
        _lastTicks = stopwatchTicks;
        LastDtSeconds = dt;

        return MetricValue<double>.Approx(_display, MetricSource.Derived, note);

        MetricValue<double> CurrentOrNone() =>
            _hasDisplay ? MetricValue<double>.Approx(_display, MetricSource.Derived, note) : MetricValue<double>.None;
    }

    /// <summary>Call when the backend identity changed or state was lost.</summary>
    public void Reset()
    {
        _lastCounter = null;
        _lastTicks = 0;
        _staleSamples = 0;
        _hasDisplay = false;
        _display = 0;
    }

    internal static readonly long StopwatchTicksPerSecond = System.Diagnostics.Stopwatch.Frequency;

    public bool HasBaseline => _lastCounter.HasValue;
}
