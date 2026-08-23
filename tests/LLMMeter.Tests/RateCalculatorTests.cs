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
    public void NonZero_Interval_Shown_Immediately()
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
    public void NonZero_Holds_Last_Value_Then_Zeroes_When_Idle()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));
        rc.Update(500, Ticks(1));        // 500 tok/s

        // within 2s hold window: still shows ~500 even though counter is flat
        var held = rc.Update(500, Ticks(2));
        Assert.InRange(held.Value, 400, 600);

        var held2 = rc.Update(500, Ticks(2.5));
        Assert.InRange(held2.Value, 400, 600);

        // past the 2s hold (last non-zero was at t=1): real zero
        var zero = rc.Update(500, Ticks(3.1));
        Assert.Equal(0, zero.Value);
    }

    [Fact]
    public void Fresh_NonZero_Resets_Hold_And_Shows_Immediately()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));
        rc.Update(500, Ticks(1));        // 500 tok/s
        rc.Update(500, Ticks(2));        // holding

        // work resumes with a big jump: must show the fresh rate at once,
        // NOT dragged down or blended with the prior held value
        var resumed = rc.Update(700, Ticks(2.5)); // 200 tokens / 0.5s = 400 tok/s
        Assert.True(resumed.Value > 0);
        Assert.InRange(resumed.Value, 350, 450);
    }

    [Fact]
    public void Rate_Is_Not_Averaged_Between_Samples()
    {
        var rc = new RateCalculator();
        rc.Update(100, Ticks(0));
        rc.Update(200, Ticks(0.5));      // 200 tok/s
        // next interval is twice the tokens over the same dt → 400 tok/s
        var v = rc.Update(400, Ticks(1.0)); // 200 more over 0.5s → 400 tok/s
        Assert.InRange(v.Value, 380, 420);
    }

    [Fact]
    public void Counter_Reset_Never_Produces_Negative_Rate()
    {
        var rc = new RateCalculator();
        rc.Update(100_000, Ticks(0));

        var before = rc.Update(101_000, Ticks(1));
        Assert.InRange(before.Value, 900, 1100);

        // server restarted: counter dropped. Within hold window → holds, not negative.
        var afterReset = rc.Update(50, Ticks(2));
        Assert.True(afterReset.Value >= 0 && afterReset.Value > 0, $"negative/flash bug! {afterReset.Value}");

        // next interval measured from the new baseline: meaningful rate again
        var next = rc.Update(250, Ticks(3)); // 200 / 1s
        Assert.True(next.Value >= 0);
    }

    [Fact]
    public void Decrease_ReBaselines_And_Holds_Then_Zeroes()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));
        rc.Update(500, Ticks(1));         // 500 tok/s

        // Counter drops (e.g. request finished, slot n_decoded reset):
        // must NOT flash to zero — hold within the 2s window.
        var afterDrop = rc.Update(0, Ticks(1.5));
        Assert.InRange(afterDrop.Value, 400, 600);

        // past the hold window → real zero
        var zero = rc.Update(0, Ticks(4));
        Assert.Equal(0, zero.Value);

        // and it can recover on the next request
        var resumed = rc.Update(100, Ticks(4.5)); // 100 / 0.5s = 200 tok/s
        Assert.InRange(resumed.Value, 150, 250);
    }
}
