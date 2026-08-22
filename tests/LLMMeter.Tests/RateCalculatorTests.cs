using LLMMeter.Collection;
using LLMMeter.Core;
using Xunit;

namespace LLMMeter.Tests;

public class RateCalculatorTests
{
    private static long Ticks(double seconds) => (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    [Fact]
    public void First_Sample_Has_No_Rate()
    {
        var rc = new RateCalculator();
        var v = rc.Update(1000, Ticks(0));
        Assert.False(v.HasValue); // no interval yet — must not invent a number
    }

    [Fact]
    public void Normal_Increments_Produce_Positive_Rate()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));

        // 500 tokens over 1s → 500 tok/s
        var v = rc.Update(500, Ticks(1.0));
        Assert.True(v.HasValue);
        Assert.Equal(MetricQuality.Approximate, v.Quality);
        Assert.InRange(v.Value, 400, 600);
    }

    [Fact]
    public void Irregular_Intervals_Are_Handled_With_Actual_Dt()
    {
        var rc = new RateCalculator(emaAlpha: 1.0); // no smoothing for exact math
        rc.Update(0, Ticks(0));

        // 100 tokens in 0.25 s → 400 tok/s
        var v1 = rc.Update(100, Ticks(0.25));
        Assert.InRange(v1.Value, 399, 401);

        // 300 more tokens in 2 s → 150 tok/s
        var v2 = rc.Update(400, Ticks(2.25));
        Assert.InRange(v2.Value, 149, 151);
    }

    [Fact]
    public void Zero_Increment_Displays_Real_Zero_After_Two_Stale_Samples()
    {
        var rc = new RateCalculator(emaAlpha: 1.0);
        rc.Update(0, Ticks(0));
        var busy = rc.Update(500, Ticks(1));
        Assert.True(busy.Value > 0);

        // one unchanged sample: still shows last value (no flicker to zero)
        var stale1 = rc.Update(500, Ticks(2));
        Assert.True(stale1.Value > 0);

        // second unchanged sample: confirmed idle → exactly 0
        var stale2 = rc.Update(500, Ticks(3));
        Assert.Equal(0, stale2.Value);
        Assert.Equal(0, rc.Update(500, Ticks(4)).Value);

        // work resumes
        var resumed = rc.Update(700, Ticks(5));
        Assert.True(resumed.Value > 0);
    }

    [Fact]
    public void Counter_Reset_Never_Produces_Negative_Rate()
    {
        var rc = new RateCalculator(emaAlpha: 1.0);
        rc.Update(100_000, Ticks(0));

        var before = rc.Update(101_000, Ticks(1));
        Assert.InRange(before.Value, 900, 1100);

        // server restarted: counter dropped
        var afterReset = rc.Update(50, Ticks(2));
        Assert.True(afterReset.Value >= 0, $"negative rate bug! {afterReset.Value}");

        // next interval is measured from the new baseline
        var next = rc.Update(250, Ticks(3));
        Assert.True(next.Value >= 0);
    }

    [Fact]
    public void Ema_Smooths_Spikes()
    {
        var rc = new RateCalculator(emaAlpha: 0.35);
        rc.Update(0, Ticks(0));
        rc.Update(1000, Ticks(1)); // ~1000 tok/s
        var smoothed = rc.Update(2000, Ticks(2)); // raw 1000 again; ema stays near 1000

        // with equal raw inputs the EMA converges toward the raw rate, never exceeds it wildly
        Assert.InRange(smoothed.Value, 800, 1200);
    }
}
