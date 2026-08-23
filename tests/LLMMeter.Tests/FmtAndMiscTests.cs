using System.Text;
using LLMMeter.Core;
using LLMMeter.Discovery;
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
