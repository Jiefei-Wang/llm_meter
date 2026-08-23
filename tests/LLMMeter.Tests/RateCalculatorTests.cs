using LLMMeter.Collection;
using LLMMeter.Core;
using Xunit;

namespace LLMMeter.Tests;

public class RateCalculatorTests
{
    private static readonly long F = System.Diagnostics.Stopwatch.Frequency;
    private static long Ticks(double seconds) => (long)(seconds * F);

    [Fact]
    public void First_Sample_Has_No_Rate()
    {
        var rc = new RateCalculator();
        var v = rc.Update(1000, Ticks(0));
        Assert.False(v.HasValue); // no interval yet — must not invent a number
    }

    [Fact]
    public void Second_Sample_Produces_Positive_Rate()
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
    public void Rolling_Two_Second_Average_Is_Used()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));

        // Steady 500 tok/s: +250 every 0.5s for 4 samples (2s window)
        for (int i = 1; i <= 4; i++)
            rc.Update(250L * i, Ticks(0.5 * i));

        // raw rate per interval is 500; averaged over the window stays ~500
        var avg = rc.Update(250L * 5, Ticks(0.5 * 5));
        Assert.InRange(avg.Value, 400, 600);
    }

    [Fact]
    public void Idle_Then_NonZero_Jumps_Immediately_No_Averaging_Down()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));
        rc.Update(500, Ticks(1));  // busy: 500 tok/s

        // two flat samples → real zero
        rc.Update(500, Ticks(2));
        var zero = rc.Update(500, Ticks(3));
        Assert.Equal(0, zero.Value);

        // work resumes with a big jump in 0.5s: must show the fresh rate at once
        // (should NOT be dragged down by the idle window averaging toward 0)
        var resumed = rc.Update(700, Ticks(3.5));
        Assert.True(resumed.Value > 0);
        Assert.InRange(resumed.Value, 350, 450); // 200 tokens / 0.5s = 400 tok/s
    }

    [Fact]
    public void Idle_Over_Window_Shows_Zero()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));
        rc.Update(500, Ticks(1));

        // 2s window still contains the busy interval: the average decays, not zeroes
        var decayed = rc.Update(500, Ticks(2));
        Assert.True(decayed.Value > 0);

        // once the busy interval leaves the 2s window, the average is a true zero
        var s2 = rc.Update(500, Ticks(3));
        Assert.Equal(0, s2.Value);
    }

    [Fact]
    public void Counter_Reset_Never_Produces_Negative_Rate()
    {
        var rc = new RateCalculator();
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
    public void Within_Window_Unchanged_Holds_Value_Until_Window_Exhausted()
    {
        var rc = new RateCalculator();
        rc.Update(100, Ticks(0));
        var busy = rc.Update(200, Ticks(0.5)); // 200 tok/s instant
        Assert.True(busy.Value > 0);

        // one flat sample following a busy one: window still contains the busy
        // interval, so the average is not yet zero
        var next = rc.Update(200, Ticks(1.0));
        Assert.True(next.Value > 0);

        // enough flat samples to push the busy interval out of the 2s window
        var far = rc.Update(200, Ticks(3.0));
        Assert.Equal(0, far.Value);
    }
}
