using LLMMeter.Collection;
using Xunit;

namespace LLMMeter.Tests;

public class RollingTtftTests
{
    private static long Ticks(double s) => (long)(s * System.Diagnostics.Stopwatch.Frequency);

    [Fact]
    public void Single_Request_Deltas_Are_Exact()
    {
        var ttft = new RollingTtft(10);
        ttft.Observe(0, 0, Ticks(0));
        ttft.Observe(1, 0.400, Ticks(1)); // one request, TTFT 400 ms
        ttft.Observe(2, 0.500, Ticks(2)); // next request, TTFT = 0.5 - 0.4 = 100 ms

        Assert.Equal((0.4 + 0.1) / 2, ttft.AverageSeconds()!.Value, 6);
        Assert.True(ttft.IsExactEstimate());
    }

    [Fact]
    public void Multi_Request_Batch_Is_Approximate()
    {
        var ttft = new RollingTtft(10);
        ttft.Observe(0, 0, Ticks(0));

        // 4 requests completed between scrapes: only their average is known
        ttft.Observe(4, 2.0, Ticks(1)); // avg = 500 ms
        Assert.Equal(0.5, ttft.AverageSeconds()!.Value, 6);
        Assert.False(ttft.IsExactEstimate());
    }

    [Fact]
    public void Mixed_Window_With_Batch_Is_Approximate()
    {
        var ttft = new RollingTtft(10);
        ttft.Observe(0, 0, Ticks(0));
        ttft.Observe(1, 0.200, Ticks(1));   // exact sample
        ttft.Observe(3, 1.000, Ticks(2));   // batch of 2 @ 400ms

        Assert.Equal((0.2 * 1 + 0.4 * 2) / 3, ttft.AverageSeconds()!.Value, 6); // weighted
        Assert.False(ttft.IsExactEstimate());
    }

    [Fact]
    public void Window_Rolls_Off_Old_Entries()
    {
        var ttft = new RollingTtft(10);
        ttft.Observe(0, 0, Ticks(0));

        // 15 single requests of 100ms each → window keeps last 10 (all 100ms)
        for (int i = 1; i <= 15; i++)
            ttft.Observe(i, i * 0.1, Ticks(i));

        Assert.Equal(0.1, ttft.AverageSeconds()!.Value, 6);
        Assert.True(ttft.IsExactEstimate()); // all weight-1 samples
        Assert.InRange(ttft.SampleCount, 1, 10);
    }

    [Fact]
    public void Histogram_Reset_Clears_State_Without_Corrupting_Average()
    {
        var ttft = new RollingTtft(10);
        ttft.Observe(0, 0, Ticks(0));
        for (int i = 1; i <= 5; i++)
            ttft.Observe(i, i * 0.3, Ticks(i));
        Assert.Equal(0.3, ttft.AverageSeconds()!.Value, 6);

        // counter reset (restart): count goes backwards
        ttft.Observe(1, 0.05, Ticks(100));

        // after reset, a fresh observation starts a clean window
        // (count 2 - 1 = 1 request; sum 0.25 - 0.05 = 200 ms)
        ttft.Observe(2, 0.25, Ticks(101));
        Assert.Equal(0.20, ttft.AverageSeconds()!.Value, 6);
    }

    [Fact]
    public void AddExact_Contributes_Weight_One()
    {
        var ttft = new RollingTtft(10);
        ttft.AddExact(0.2);
        ttft.AddExact(0.4);
        Assert.Equal(0.3, ttft.AverageSeconds()!.Value, 6);
        // both samples came from single-request deltas → still exact
        Assert.True(ttft.IsExactEstimate());
    }
}
