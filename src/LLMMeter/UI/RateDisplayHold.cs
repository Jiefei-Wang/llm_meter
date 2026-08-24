using LLMMeter.Core;

namespace LLMMeter.UI;

/// <summary>Keeps the last non-zero rate visible briefly without altering graph history.</summary>
internal sealed class RateDisplayHold(TimeSpan? duration = null)
{
    private readonly TimeSpan _duration = duration ?? TimeSpan.FromSeconds(2);
    private MetricValue<double> _lastNonZero = MetricValue<double>.None;
    private DateTimeOffset _lastNonZeroAt;

    public MetricValue<double> Update(MetricValue<double> current, DateTimeOffset now)
    {
        if (!current.HasValue) return current;
        if (current.Value > 0)
        {
            _lastNonZero = current;
            _lastNonZeroAt = now;
            return current;
        }
        return _lastNonZero.HasValue && now - _lastNonZeroAt < _duration
            ? _lastNonZero
            : current;
    }

    public void Reset()
    {
        _lastNonZero = MetricValue<double>.None;
        _lastNonZeroAt = default;
    }
}
