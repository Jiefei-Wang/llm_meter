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
        Assert.False(rc.Update(1000, Ticks(0)).HasValue);
    }

    [Fact]
    public void NonZero_Interval_Is_Current_Unaveraged_Value()
    {
        var rc = new RateCalculator();
        rc.Update(100, Ticks(0));
        Assert.InRange(rc.Update(200, Ticks(0.5)).Value, 199.9, 200.1);
        Assert.InRange(rc.Update(400, Ticks(1)).Value, 399.9, 400.1);
    }

    [Fact]
    public void Flat_Counter_Immediately_Produces_Raw_Zero()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));
        rc.Update(500, Ticks(1));
        Assert.Equal(0, rc.Update(500, Ticks(2)).Value);
    }

    [Fact]
    public void Fresh_NonZero_After_Zero_Is_Shown_Immediately()
    {
        var rc = new RateCalculator();
        rc.Update(0, Ticks(0));
        rc.Update(500, Ticks(1));
        rc.Update(500, Ticks(2));
        Assert.InRange(rc.Update(700, Ticks(2.5)).Value, 399.9, 400.1);
    }

    [Fact]
    public void Counter_Reset_Rebaselines_Without_Negative_Rate()
    {
        var rc = new RateCalculator();
        rc.Update(100_000, Ticks(0));
        Assert.InRange(rc.Update(101_000, Ticks(1)).Value, 999.9, 1000.1);
        Assert.False(rc.Update(50, Ticks(2)).HasValue);
        Assert.InRange(rc.Update(250, Ticks(3)).Value, 199.9, 200.1);
    }
}
