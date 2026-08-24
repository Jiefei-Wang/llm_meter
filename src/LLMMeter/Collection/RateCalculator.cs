using System.Diagnostics;
using LLMMeter.Core;

namespace LLMMeter.Collection;

/// <summary>Converts a monotonic counter into the raw rate for the latest interval.</summary>
public sealed class RateCalculator
{
    private double? _lastCounter;
    private long _lastTicks;

    public double LastDtSeconds { get; private set; }

    public MetricValue<double> Update(double counter, long stopwatchTicks)
    {
        if (_lastCounter.HasValue && counter < _lastCounter.Value - 1e-9)
        {
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            LastDtSeconds = 0;
            return MetricValue<double>.None;
        }
        if (_lastCounter is null)
        {
            _lastCounter = counter;
            _lastTicks = stopwatchTicks;
            return MetricValue<double>.None;
        }

        double dt = (double)(stopwatchTicks - _lastTicks) / Stopwatch.Frequency;
        double delta = counter - _lastCounter.Value;
        _lastCounter = counter;
        _lastTicks = stopwatchTicks;
        LastDtSeconds = dt;

        if (dt <= 0.0005) return MetricValue<double>.None;
        double rate = delta > 1e-9 ? delta / dt : 0;
        return MetricValue<double>.Approx(rate, MetricSource.Derived,
            delta > 1e-9 ? $"rate over {dt:0.#}s" : "no progress");
    }

    public void Reset()
    {
        _lastCounter = null;
        _lastTicks = 0;
        LastDtSeconds = 0;
    }

    internal static readonly long StopwatchTicksPerSecond = Stopwatch.Frequency;
    public bool HasBaseline => _lastCounter.HasValue;
}
