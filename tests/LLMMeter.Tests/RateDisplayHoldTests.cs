using LLMMeter.Core;
using LLMMeter.UI;
using Xunit;

namespace LLMMeter.Tests;

public class RateDisplayHoldTests
{
    [Fact]
    public void Holds_Zero_For_Two_Seconds_But_Uses_Fresh_Work_Immediately()
    {
        var hold = new RateDisplayHold();
        var start = DateTimeOffset.UtcNow;

        Assert.Equal(100, hold.Update(MetricValue<double>.Approx(100), start).Value);
        Assert.Equal(100, hold.Update(MetricValue<double>.Approx(0), start.AddSeconds(1.9)).Value);
        Assert.Equal(250, hold.Update(MetricValue<double>.Approx(250), start.AddSeconds(1.95)).Value);
        Assert.Equal(250, hold.Update(MetricValue<double>.Approx(0), start.AddSeconds(3.9)).Value);
        Assert.Equal(0, hold.Update(MetricValue<double>.Approx(0), start.AddSeconds(3.96)).Value);
    }
}
