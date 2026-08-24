using System.Text;
using LLMMeter.Core;
using LLMMeter.Discovery;
using LLMMeter.UI;
using Xunit;

namespace LLMMeter.Tests;

public class FmtTests
{
    [Theory]
    [InlineData(0, "0/s")]
    [InlineData(48.2, "48.2/s")]
    [InlineData(99.96, "100/s")]
    [InlineData(186.4, "186/s")]
    [InlineData(1420, "1.42k/s")]
    [InlineData(14200, "14.2k/s")]
    [InlineData(1280000, "1.28M/s")]
    public void Rates_Use_Compact_Notation(double v, string expected)
    {
        Assert.Equal(expected, Fmt.Rate(v));
    }

    [Theory]
    [InlineData(12400, "12.4k")]
    [InlineData(1280000, "1.28M")]
    [InlineData(183, "183")]
    [InlineData(7.5, "7.5")]
    public void Token_Counts_Are_Compact(double v, string expected)
    {
        Assert.Equal(expected, Fmt.Tokens(v));
    }

    [Fact]
    public void Latency_Formatting()
    {
        Assert.Equal("418 ms", Fmt.Milliseconds(418));
        Assert.Equal("1.24 s", Fmt.Milliseconds(1240));
    }

    [Fact]
    public void Approximate_Metrics_Get_Tilde_Unavailable_Get_Dash()
    {
        var exact = MetricValue<double>.Exact(186, MetricSource.NativeMetrics);
        var approx = MetricValue<double>.Approx(186);
        var none = MetricValue<double>.None;

        Assert.Equal("186/s", Fmt.Metric(exact, Fmt.Rate));
        Assert.Equal("~186/s", Fmt.Metric(approx, Fmt.Rate));
        Assert.Equal("—", Fmt.Metric(none, Fmt.Rate));
    }
}

public class ProcessRunnerDecodeTests
{
    [Fact]
    public void Decodes_UTF16_Wsl_Output()
    {
        // wsl.exe --list --quiet emits UTF-16LE like "U\0b\0u\0n\0t\0u\0-\0..."
        byte[] raw = Encoding.Unicode.GetBytes("Ubuntu-24.04\r\nDebian\r\n");
        var decoded = ProcessRunner.DecodeAuto(raw);
        Assert.Equal("Ubuntu-24.04\r\nDebian\r\n", decoded);

        // Ordinal on purpose: ICU collation treats '\0' as weightless, so a
        // culture-sensitive Contains("\0") matches EVERY string on .NET 5+.
        Assert.True(decoded.IndexOf('\u0000') < 0);
    }

    [Fact]
    public void Decodes_UTF8_With_BOM_And_UTF16_With_BOM()
    {
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
        Assert.Equal("hello", ProcessRunner.DecodeAuto(utf8));

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("héllo")).ToArray();
        Assert.Equal("héllo", ProcessRunner.DecodeAuto(utf16));
    }

    [Fact]
    public void Lines_Splits_And_Trims()
    {
        var lines = ProcessRunner.Lines("Ubuntu-24.04\r\nDebian\n\n  \n");
        Assert.Equal(["Ubuntu-24.04", "Debian"], lines);
    }
}

public class PortMappingTests
{
    [Fact]
    public void Network_Port_Bytes_Are_Swapped_Correctly()
    {
        // GetExtendedTcpTable returns ports in network byte order in the low word:
        // port 8080 (0x1F90) appears as 0x901F.
        uint stored = 0x0000901F;
        Assert.Equal((ushort)8080, WindowsProcessDiscovery.SwapPort(stored));
    }

    [Fact]
    public void V4_Addresses_Format_In_Host_Order()
    {
        // 127.0.0.1 arrives as little-endian dword 0x0100007F
        Assert.Equal("127.0.0.1", WindowsProcessDiscovery.FormatV4(0x0100007F));
        Assert.Equal("0.0.0.0", WindowsProcessDiscovery.FormatV4(0x00000000));
    }
}

public class WindowZOrderTests
{
    [Fact]
    public void Topmost_Application_Does_Not_Move_Size_Or_Activate_Window()
    {
        Assert.NotEqual(0u, WindowZOrder.ApplyFlags & WindowZOrder.NoMove);
        Assert.NotEqual(0u, WindowZOrder.ApplyFlags & WindowZOrder.NoSize);
        Assert.NotEqual(0u, WindowZOrder.ApplyFlags & WindowZOrder.NoActivate);
        Assert.NotEqual(0u, WindowZOrder.ApplyFlags & WindowZOrder.NoOwnerZOrder);
    }
}

public class RateHistoryTests
{
    [Fact]
    public void Keeps_Only_Five_Minutes_And_Preserves_Unavailable_Gaps()
    {
        var history = new RateHistory();
        var now = DateTimeOffset.Now;
        history.Record(Snapshot(now.AddMinutes(-6), 10, 20));
        history.Record(Snapshot(now.AddMinutes(-4), 30, 40));
        history.Record(Snapshot(now.AddMinutes(-3), null, 50));

        var prefill = history.PrefillSnapshot(now);
        var generate = history.GenerateSnapshot(now);

        Assert.Equal(2, prefill.Count);
        Assert.Equal(30, prefill[0].Value);
        Assert.Null(prefill[1].Value);
        Assert.Equal([40d, 50d], generate.Select(p => p.Value!.Value));
    }

    [Fact]
    public void Duplicate_Snapshot_Timestamps_Are_Not_Stored_Twice()
    {
        var history = new RateHistory();
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot(now, 1, 2);
        history.Record(snapshot);
        history.Record(snapshot);

        Assert.Single(history.PrefillSnapshot(now));
        Assert.Single(history.GenerateSnapshot(now));
    }

    private static MetricSnapshot Snapshot(DateTimeOffset timestamp, double? prefill, double? generate) => new()
    {
        Timestamp = timestamp,
        State = ConnectionState.Online,
        Kind = BackendKind.Vllm,
        PrefillTokPerSec = prefill.HasValue ? MetricValue<double>.Approx(prefill.Value) : MetricValue<double>.None,
        GenerationTokPerSec = generate.HasValue ? MetricValue<double>.Approx(generate.Value) : MetricValue<double>.None,
    };
}

public class ActivityChartTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.8, 1)]
    [InlineData(7.75, 10)]
    [InlineData(56.6, 75)]
    [InlineData(120, 200)]
    [InlineData(2300, 2500)]
    public void Y_Axis_Maximum_Rounds_Up_To_A_Readable_Value(double peak, double expected)
    {
        Assert.Equal(expected, ActivityChart.NiceScaleMaximum(peak));
    }
}

public class RequestSlotListTests
{
    private static RequestSnapshot Request(string id, long input, long prefilled, long output = 0, long cached = 0,
        double? rate = null) => new()
    {
        Id = id,
        InputTokens = MetricValue<long>.Exact(input),
        CachedTokens = MetricValue<long>.Exact(cached),
        PrefilledTokens = MetricValue<long>.Exact(prefilled),
        OutputTokens = MetricValue<long>.Exact(output),
        TokensPerSecond = rate.HasValue ? MetricValue<double>.Approx(rate.Value) : MetricValue<double>.None,
    };

    [Fact]
    public void Row_Shows_Input_Cached_Evaluated_And_Output_Separately()
    {
        var slots = new RequestSlotList();
        slots.Update([Request("#1", 1086, 1071, 10, 15, 1420)], DateTimeOffset.UtcNow);

        Assert.Contains("IN  1.09k", slots.Rows[0].MetricsText);
        Assert.Contains("CACHED     15", slots.Rows[0].MetricsText);
        Assert.Contains("EVAL  1.07k", slots.Rows[0].MetricsText);
        Assert.Contains("OUT     10", slots.Rows[0].MetricsText);
        Assert.EndsWith("1.4k/s", slots.Rows[0].MetricsText);
        Assert.DoesNotContain("~", slots.Rows[0].MetricsText);
    }

    [Fact]
    public void Input_Updates_While_Prompt_Is_Loaded()
    {
        var slots = new RequestSlotList();
        var now = DateTimeOffset.UtcNow;
        slots.Update([Request("#1", 1086, 100, 2)], now);
        slots.Update([Request("#1", 9999, 700, 20)], now.AddSeconds(1));

        Assert.Contains("IN    10k", slots.Rows[0].MetricsText);
        Assert.Contains("EVAL    700", slots.Rows[0].MetricsText);
        Assert.Contains("OUT     20", slots.Rows[0].MetricsText);
    }

    [Fact]
    public void Completed_Row_Lingers_Then_Becomes_A_Stable_Hole_Reused_First()
    {
        var slots = new RequestSlotList();
        var start = DateTimeOffset.UtcNow;
        slots.Update([Request("A", 10, 10), Request("B", 20, 20)], start);
        var first = slots.Rows[0];
        var second = slots.Rows[1];

        slots.Update([Request("B", 20, 20)], start.AddSeconds(1));
        Assert.Same(first, slots.Rows[0]);
        Assert.Contains("completed", slots.Rows[0].PrimaryText);

        slots.Advance(start.AddSeconds(3.9));
        Assert.True(slots.Rows[0].IsCompleted);
        slots.Advance(start.AddSeconds(4));
        Assert.True(slots.Rows[0].IsEmpty);
        Assert.Same(second, slots.Rows[1]);

        slots.Update([Request("B", 20, 20), Request("C", 30, 5)], start.AddSeconds(5));
        Assert.Equal("C", slots.Rows[0].RequestId);
        Assert.Same(second, slots.Rows[1]);
    }

    [Fact]
    public void Adds_All_Concurrent_Requests_Without_A_Five_Row_Cap()
    {
        var slots = new RequestSlotList();
        var requests = Enumerable.Range(1, 8).Select(i => Request($"#{i}", i, i)).ToArray();
        slots.Update(requests, DateTimeOffset.UtcNow);
        Assert.Equal(8, slots.Rows.Count);
    }

    [Fact]
    public void Removes_Only_Trailing_Holes_After_Ten_Empty_Seconds()
    {
        var slots = new RequestSlotList();
        var start = DateTimeOffset.UtcNow;
        slots.Update([Request("A", 1, 1), Request("B", 2, 2), Request("C", 3, 3)], start);
        slots.Update([Request("B", 2, 2)], start.AddSeconds(1));
        slots.Advance(start.AddSeconds(4));

        Assert.Equal(3, slots.Rows.Count);
        Assert.True(slots.Rows[0].IsEmpty); // interior hole is preserved
        Assert.True(slots.Rows[2].IsEmpty); // trailing hole can age out

        slots.Advance(start.AddSeconds(13.9));
        Assert.Equal(3, slots.Rows.Count);
        slots.Advance(start.AddSeconds(14));
        Assert.Equal(2, slots.Rows.Count);
        Assert.True(slots.Rows[0].IsEmpty);
        Assert.Equal("B", slots.Rows[1].RequestId);
    }
}

public class WidgetMetricFormattingTests
{
    private readonly LLMMeter.UI.MonitorWindowViewModel _vm = new();

    private static MetricSnapshot Snap(
        MetricValue<int>? running = null, MetricValue<int>? queued = null,
        MetricValue<long>? generated = null, MetricValue<long>? prefilled = null) => new()
    {
        Timestamp = DateTimeOffset.Now,
        State = ConnectionState.Limited,
        Kind = BackendKind.LlamaCpp,
        Running = running ?? MetricValue<int>.None,
        Queued = queued ?? MetricValue<int>.None,
        GeneratedTokensTotal = generated ?? MetricValue<long>.None,
        PrefilledTokensTotal = prefilled ?? MetricValue<long>.None,
    };

    [Fact]
    public void Running_Queue_Combine_As_X_Over_Y()
    {
        Assert.Equal("1/0", _vm.MetricRunningQueue(Snap(running: MetricValue<int>.Exact(1), queued: MetricValue<int>.Exact(0))));
        Assert.Equal("2/3", _vm.MetricRunningQueue(Snap(running: MetricValue<int>.Exact(2), queued: MetricValue<int>.Exact(3))));
    }

    [Fact]
    public void Queue_Unavailable_Shows_Running_Over_Dash()
    {
        // llama.cpp /slots exposes no queue → honest "x/—"
        Assert.Equal("1/—", _vm.MetricRunningQueue(Snap(running: MetricValue<int>.Exact(1))));
    }

    [Fact]
    public void Generated_Total_Uses_Compact_Units()
    {
        Assert.Equal("12.4k", _vm.MetricGeneratedTotal(Snap(generated: MetricValue<long>.Approx(12400))));
        Assert.Equal("1.28M", _vm.MetricGeneratedTotal(Snap(generated: MetricValue<long>.Approx(1280000))));
        Assert.Equal("—", _vm.MetricGeneratedTotal(Snap()));
    }

    [Fact]
    public void Prefilled_Total_Uses_Compact_Units()
    {
        Assert.Equal("5.2k", _vm.MetricPrefilledTotal(Snap(prefilled: MetricValue<long>.Approx(5200))));
        Assert.Equal("2.4M", _vm.MetricPrefilledTotal(Snap(prefilled: MetricValue<long>.Approx(2400000))));
        Assert.Equal("—", _vm.MetricPrefilledTotal(Snap()));
    }
}
