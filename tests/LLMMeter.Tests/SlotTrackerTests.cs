using LLMMeter.Adapters;
using Xunit;

namespace LLMMeter.Tests;

/// <summary>
/// /slots derivation rules (spec §30): per-slot generation rate only while the
/// same task continues on a slot; no fabricated prefill/queue.
/// </summary>
public class SlotTrackerTests
{
    private static long Ticks(double s) => (long)(s * System.Diagnostics.Stopwatch.Frequency);

    [Fact]
    public void Slot_Start_Has_No_Rate()
    {
        var tracker = new SlotTracker(emaAlpha: 1.0);
        var r = tracker.Observe(slotId: 0, taskId: 100, promptTokens: 42, decoded: 1, nowTicks: Ticks(0), processing: true);

        Assert.NotNull(r);
        Assert.False(r!.TokensPerSecond.HasValue); // baseline sample
        Assert.Equal("#100", r.Id);
        Assert.Equal(42, r.InputTokens.Value);
        Assert.Equal(1, r.OutputTokens.Value);
    }

    [Fact]
    public void Slot_Continuation_Derives_Rate()
    {
        var tracker = new SlotTracker(emaAlpha: 1.0);
        _ = tracker.Observe(0, 100, 42, 5, Ticks(0), true);

        // 20 more tokens in 0.5 s → 40 tok/s
        var r = tracker.Observe(0, 100, 42, 25, Ticks(0.5), true);
        Assert.True(r!.TokensPerSecond.HasValue);
        Assert.InRange(r.TokensPerSecond.Value, 39.9, 40.1);
    }

    [Fact]
    public void Slot_Completion_Stops_Reporting()
    {
        var tracker = new SlotTracker(emaAlpha: 1.0);
        _ = tracker.Observe(0, 100, 10, 3, Ticks(0), true);
        _ = tracker.Observe(0, 100, 10, 8, Ticks(0.5), true);

        // slot finished: is_processing false → row not produced
        var done = tracker.Observe(0, 100, 10, 9, Ticks(1.0), processing: false);
        Assert.Null(done);
    }

    [Fact]
    public void Task_Id_Reuse_Rebaselines_Without_Fake_Rate()
    {
        var tracker = new SlotTracker(emaAlpha: 1.0);
        // first request ends with 200 decoded tokens
        _ = tracker.Observe(2, 7, 10, 200, Ticks(0), true);
        _ = tracker.Observe(2, 7, 10, 210, Ticks(1), true); // ~10 tok/s

        // NEW request reuses the slot; its counter restarts near zero.
        // n_decoded < last value must NOT be treated as a reset producing garbage,
        // and the new baseline must not inherit the old rate.
        var fresh = tracker.Observe(2, 8, 55, 2, Ticks(2), true);
        Assert.NotNull(fresh);
        Assert.Equal("#8", fresh!.Id);
        Assert.False(fresh.TokensPerSecond.HasValue); // no rate until next delta

        var next = tracker.Observe(2, 8, 55, 22, Ticks(2.5), true);
        Assert.InRange(next!.TokensPerSecond.Value, 39.9, 40.1); // (22-2)/0.5
    }

    [Fact]
    public void Counter_Reset_On_Same_Task_Never_Goes_Negative()
    {
        var tracker = new SlotTracker(emaAlpha: 1.0);
        _ = tracker.Observe(1, 50, 10, 300, Ticks(0), true);
        _ = tracker.Observe(1, 50, 10, 320, Ticks(1), true); // +20

        // pathological: decoded dropped without a task change
        var weird = tracker.Observe(1, 50, 10, 310, Ticks(2), true);
        if (weird!.TokensPerSecond.HasValue)
            Assert.True(weird.TokensPerSecond.Value >= 0);
    }

    [Fact]
    public void Multiple_Simultaneous_Slots_Track_Independently()
    {
        var tracker = new SlotTracker(emaAlpha: 1.0);
        _ = tracker.Observe(0, 1, 5, 0, Ticks(0), true);
        _ = tracker.Observe(1, 2, 5, 0, Ticks(0), true);

        _ = tracker.Observe(0, 1, 5, 10, Ticks(1), true); // slot0: 10/s
        _ = tracker.Observe(1, 2, 5, 40, Ticks(1), true); // slot1: 40/s

        var r0 = tracker.Observe(0, 1, 5, 20, Ticks(2), true);
        var r1 = tracker.Observe(1, 2, 5, 80, Ticks(2), true);

        Assert.InRange(r0!.TokensPerSecond.Value, 9.9, 10.1);
        Assert.InRange(r1!.TokensPerSecond.Value, 39.9, 40.1);
    }
}
