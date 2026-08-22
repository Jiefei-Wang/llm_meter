namespace LLMMeter.Core;

/// <summary>Where a metric value came from.</summary>
public enum MetricSource
{
    NativeMetrics,   // backend exposes the metric natively (e.g. Prometheus /metrics)
    NativeApi,       // read directly from an official API endpoint (e.g. /slots)
    Derived,         // computed from other native values (e.g. counter deltas)
    CaptureProxy,    // measured via request capture (future)
}

/// <summary>Confidence in a metric value. Unavailable is different from zero.</summary>
public enum MetricQuality
{
    Exact,
    Approximate,
    Unavailable,
}

/// <summary>
/// A metric value with provenance. A metric that cannot be determined is
/// represented as Quality=Unavailable and must never be faked as zero.
/// </summary>
public sealed class MetricValue<T>
{
    public static readonly MetricValue<T> None =
        new(default!, MetricQuality.Unavailable, MetricSource.NativeApi);

    public T Value { get; }
    public MetricQuality Quality { get; }
    public MetricSource Source { get; }
    public string? Note { get; }

    public bool HasValue => Quality != MetricQuality.Unavailable;

    public MetricValue(T value, MetricQuality quality, MetricSource source, string? note = null)
    {
        Value = value;
        Quality = quality;
        Source = source;
        Note = note;
    }

    public static MetricValue<T> Exact(T value, MetricSource src = MetricSource.NativeMetrics, string? note = null) =>
        new(value, MetricQuality.Exact, src, note);

    public static MetricValue<T> Approx(T value, MetricSource src = MetricSource.Derived, string? note = null) =>
        new(value, MetricQuality.Approximate, src, note);

    public override string ToString() => Quality switch
    {
        MetricQuality.Unavailable => "—",
        MetricQuality.Exact => $"{Value}",
        _ => $"~{Value}",
    };
}
