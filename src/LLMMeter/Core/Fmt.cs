using System.Globalization;

namespace LLMMeter.Core;

/// <summary>Compact numeric formatting for the widget (see spec §42).</summary>
public static class Fmt
{
    public static string Rate(double tokPerSec) => tokPerSec switch
    {
        >= 1_000_000 => $"{Compact(tokPerSec / 1_000_000)}M/s",
        >= 1000 => $"{Compact(tokPerSec / 1000)}k/s",
        >= 100 => $"{tokPerSec:0}/s",
        >= 10 => $"{tokPerSec:0.#}/s",
        _ => $"{tokPerSec:0.##}/s",
    };

    public static string Tokens(double tokens) => tokens switch
    {
        >= 1_000_000 => $"{Compact(tokens / 1_000_000)}M",
        >= 1000 => $"{Compact(tokens / 1000)}k",
        >= 10 => $"{tokens:0}",
        _ => $"{tokens:0.#}",
    };

    public static string Count(int n) =>
        Math.Abs(n) >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" :
        Math.Abs(n) >= 10_000 ? $"{n / 1000.0:0.#}k" :
        n.ToString(CultureInfo.InvariantCulture);

    public static string Milliseconds(double ms) => ms switch
    {
        >= 1000 => $"{ms / 1000:0.##} s",
        >= 100 => $"{ms:0} ms",
        _ => $"{ms:0.#} ms",
    };

    public static string Percent(double fraction) => $"{fraction * 100:0}%";

    /// <summary>Value with ~ marker when approximate; — when unavailable.</summary>
    public static string Metric(MetricValue<double> v, Func<double, string> format)
    {
        if (!v.HasValue) return "—";
        var s = format(v.Value);
        return v.Quality == MetricQuality.Approximate ? "~" + s : s;
    }

    private static string Compact(double d) =>
        d >= 100 ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString("0.##", CultureInfo.InvariantCulture);
}
