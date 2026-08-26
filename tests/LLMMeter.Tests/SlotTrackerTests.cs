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
        var tracker = new SlotTracker();
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
        var tracker = new SlotTracker();
        _ = tracker.Observe(0, 100, 42, 5, Ticks(0), true);

        // 20 more tokens in 0.5 s → 40 tok/s
        var r = tracker.Observe(0, 100, 42, 25, Ticks(0.5), true);
        Assert.True(r!.TokensPerSecond.HasValue);
        Assert.InRange(r.TokensPerSecond.Value, 39.9, 40.1);
    }

    [Fact]
    public void Slot_Continuation_Derives_Prefill_Rate()
    {
        var tracker = new SlotTracker();
        _ = tracker.Observe(0, 100, 1000, 0, Ticks(0), true, prefilledTokens: 100);

        var r = tracker.Observe(0, 100, 1000, 0, Ticks(0.5), true, prefilledTokens: 500);

        Assert.True(r!.PrefillTokensPerSecond.HasValue);
        Assert.InRange(r.PrefillTokensPerSecond.Value, 799.9, 800.1);
    }

    [Fact]
    public void Batched_Prefill_Uses_Time_Since_Previous_Counter_Change()
    {
        var tracker = new SlotTracker();
        _ = tracker.Observe(0, 100, 10_000, 0, Ticks(0), true, prefilledTokens: 4096);
        var zero1 = tracker.Observe(0, 100, 10_000, 0, Ticks(0.5), true, prefilledTokens: 4096);
        var zero2 = tracker.Observe(0, 100, 10_000, 0, Ticks(1.0), true, prefilledTokens: 4096);
        var batch = tracker.Observe(0, 100, 10_000, 0, Ticks(1.85), true, prefilledTokens: 6144);

        Assert.Equal(0, zero1!.PrefillTokensPerSecond.Value);
        Assert.Equal(0, zero2!.PrefillTokensPerSecond.Value);
        Assert.InRange(batch!.PrefillTokensPerSecond.Value, 1106, 1108);
    }

    [Fact]
    public void Batched_Decode_Uses_Time_Since_Previous_Counter_Change()
    {
        var tracker = new SlotTracker();
        _ = tracker.Observe(0, 100, 100, 10, Ticks(0), true, prefilledTokens: 100);
        _ = tracker.Observe(0, 100, 100, 10, Ticks(0.5), true, prefilledTokens: 100);
        var batch = tracker.Observe(0, 100, 100, 30, Ticks(1.0), true, prefilledTokens: 100);

        Assert.InRange(batch!.TokensPerSecond.Value, 19.9, 20.1);
    }

    [Fact]
    public void Slot_Completion_Stops_Reporting()
    {
        var tracker = new SlotTracker();
        _ = tracker.Observe(0, 100, 10, 3, Ticks(0), true);
        _ = tracker.Observe(0, 100, 10, 8, Ticks(0.5), true);

        // slot finished: is_processing false → row not produced
        var done = tracker.Observe(0, 100, 10, 9, Ticks(1.0), processing: false);
        Assert.Null(done);
    }

    [Fact]
    public void Task_Id_Reuse_Rebaselines_Without_Fake_Rate()
    {
        var tracker = new SlotTracker();
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
        var tracker = new SlotTracker();
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
        var tracker = new SlotTracker();
        _ = tracker.Observe(0, 1, 5, 0, Ticks(0), true);
        _ = tracker.Observe(1, 2, 5, 0, Ticks(0), true);

        _ = tracker.Observe(0, 1, 5, 10, Ticks(1), true); // slot0: 10/s
        _ = tracker.Observe(1, 2, 5, 40, Ticks(1), true); // slot1: 40/s

        var r0 = tracker.Observe(0, 1, 5, 20, Ticks(2), true);
        var r1 = tracker.Observe(1, 2, 5, 80, Ticks(2), true);

        Assert.InRange(r0!.TokensPerSecond.Value, 9.9, 10.1);
        Assert.InRange(r1!.TokensPerSecond.Value, 39.9, 40.1);
    }

    [Fact]
    public void Slot_Prefill_Rate_On_Task_Change_Rebaselines_Without_Fake_Rate()
    {
        var tracker = new SlotTracker();
        // Task 10 prefilling
        _ = tracker.Observe(0, 10, 1000, 0, Ticks(0), true, prefilledTokens: 200);
        var r1 = tracker.Observe(0, 10, 1000, 0, Ticks(0.5), true, prefilledTokens: 600);
        Assert.True(r1!.PrefillTokensPerSecond.HasValue);
        Assert.InRange(r1.PrefillTokensPerSecond.Value, 799.9, 800.1);

        // New task 11 starts prefilling on the same slot
        var rNew = tracker.Observe(0, 11, 2000, 0, Ticks(1.0), true, prefilledTokens: 100);
        Assert.NotNull(rNew);
        Assert.Equal("#11", rNew!.Id);
        Assert.False(rNew.PrefillTokensPerSecond.HasValue); // no rate on baseline sample of new task

        // Subsequent delta derives rate accurately
        var rNext = tracker.Observe(0, 11, 2000, 0, Ticks(1.5), true, prefilledTokens: 500);
        Assert.True(rNext!.PrefillTokensPerSecond.HasValue);
        Assert.InRange(rNext.PrefillTokensPerSecond.Value, 799.9, 800.1);
    }

    [Fact]
    public async Task Cumulative_Slot_Totals_Do_Not_Cross_Contaminate_Task_Ids()
    {
        var adapter = new LlamaCppAdapter();
        // Fallback slots-only mode (metrics disabled)
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (-1, ""),
            ["props"] = (200, """{"total_slots":1,"model_path":"/models/test.gguf"}"""),
            ["slots"] = (200, ""),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);

        // Poll 1: Slot 0, Task 100 baseline
        routes["slots"] = (200, """[{"id":0,"is_processing":true,"id_task":100,"n_decoded":10,"n_prompt_tokens_processed":1000}]""");
        await adapter.CollectAsync(http, default);

        // Poll 2: Slot 0, Task 100 progress: decoded 10 -> 25 (+15), processed 1000 -> 1500 (+500)
        routes["slots"] = (200, """[{"id":0,"is_processing":true,"id_task":100,"n_decoded":25,"n_prompt_tokens_processed":1500}]""");
        var snap2 = await adapter.CollectAsync(http, default);
        Assert.Equal(15, snap2.GeneratedTokensTotal.Value);
        Assert.Equal(500, snap2.PrefilledTokensTotal.Value);

        // Poll 3: Slot 0 reused for Task 101 with lower starting counters!
        // Decoded drops from 25 to 5, processed drops from 1500 to 200.
        // Must NOT decrease totals, fabricate negative delta, or add 5 as delta.
        routes["slots"] = (200, """[{"id":0,"is_processing":true,"id_task":101,"n_decoded":5,"n_prompt_tokens_processed":200}]""");
        var snap3 = await adapter.CollectAsync(http, default);
        Assert.Equal(15, snap3.GeneratedTokensTotal.Value);
        Assert.Equal(500, snap3.PrefilledTokensTotal.Value);

        // Poll 4: Slot 0, Task 101 progress: decoded 5 -> 15 (+10), processed 200 -> 600 (+400)
        routes["slots"] = (200, """[{"id":0,"is_processing":true,"id_task":101,"n_decoded":15,"n_prompt_tokens_processed":600}]""");
        var snap4 = await adapter.CollectAsync(http, default);
        // Total generated = 15 + 10 = 25
        Assert.Equal(25, snap4.GeneratedTokensTotal.Value);
        // Total prefilled = 500 + 400 = 900
        Assert.Equal(900, snap4.PrefilledTokensTotal.Value);
    }
}
