using System.Diagnostics;
using LLMMeter.Core;

namespace LLMMeter.Collection;

/// <summary>
/// Converts a monotonically increasing counter into a tok/s rate. There is no
/// averaging: each non-zero interval is shown as-is. When the counter stops
/// increasing (rate would be 0), the last non-zero value is HELD for
/// <c>holdSeconds</c> (default 2 s) before showing a real zero, so a busy
/// generator never flickers to 0 between token batches. A fresh non-zero
/// interval resets the hold and is shown immediately. Counter resets (server
/// restart) never produce a negative rate.
/// </summary>
public sealed class RateCalculator(double holdSeconds = 2.0)
{
    private readonly double _holdSeconds = Math.Clamp(holdSeconds, 0.1, 60.0);

    private double? _lastCounter;
    private long _lastTicks;              // Stopwatch ticks (monotonic)
    private bool _hasDisplay;
    private double _display;

    private double _lastNonZero;
    private long _lastNonZeroTicks;

    public const double DefaultHoldSeconds = 2.0;

    /// <summary>Seconds between the last two accepted samples (diagnostics).</summary>
    public double LastDtSeconds { get; private set; }

    /// <param name="counter">Cumulative counter value.</param>
    /// <param name="stopwatchTicks">Monotonic clock reading for this sample.</param>
    public MetricValue<double> Update(double counter, long stopwatchTicks)
    {
        // Counter reset (server restart). Discard state, re-baseline.
        if (_lastCounter.HasValue && counter < _lastCounter.Value - 1e-9)
        {
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            _hasDisplay = false;
            _display = 0;
            _lastNonZero = 0;
            _lastNonZeroTicks = 0;
            return MetricValue<double>.Approx(0, MetricSource.Derived, "counter reset; baseline reset");
        }

        if (_lastCounter is null)
        {
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            _lastNonZeroTicks = stopwatchTicks;
            return MetricValue<double>.None; // no interval yet — not a rate
        }

        double dt = (double)(stopwatchTicks - _lastTicks) / Stopwatch.Frequency;
        double delta = counter - _lastCounter.Value;

        _lastCounter = counter;
        _lastTicks = stopwatchTicks;
        LastDtSeconds = dt;

        if (dt <= 0.0005)
            return CurrentOrNone();

        MetricValue<double> result;
        if (delta > 1e-9)
        {
            // Non-zero interval: show it at once and arm the hold timer.
            double rate = delta / dt;
            _lastNonZero = rate;
            _lastNonZeroTicks = stopwatchTicks;
            _display = rate;
            _hasDisplay = true;
            result = MetricValue<double>.Approx(rate, MetricSource.Derived, $"rate over {dt:0.#}s");
        }
        else if (_hasDisplay && stopwatchTicks - _lastNonZeroTicks < (long)(_holdSeconds * Stopwatch.Frequency))
        {
            // No progress, but within the hold window: keep the last non-zero.
            _display = _lastNonZero;
            result = MetricValue<double>.Approx(_lastNonZero, MetricSource.Derived, "holding last rate");
        }
        else
        {
            // Confirmed idle past the hold window: real zero.
            _display = 0;
            _hasDisplay = true;
            result = MetricValue<double>.Approx(0, MetricSource.Derived, "idle");
        }

        return result;

        MetricValue<double> CurrentOrNone() =>
            _hasDisplay ? MetricValue<double>.Approx(_display, MetricSource.Derived, "too soon") : MetricValue<double>.None;
    }

    /// <summary>Call when the backend identity changed or state was lost.</summary>
    public void Reset()
    {
        _lastCounter = null;
        _lastTicks = 0;
        _hasDisplay = false;
        _display = 0;
        _lastNonZero = 0;
        _lastNonZeroTicks = 0;
    }

    internal static readonly long StopwatchTicksPerSecond = System.Diagnostics.Stopwatch.Frequency;

    public bool HasBaseline => _lastCounter.HasValue;
}
