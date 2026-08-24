using System.IO;
using LLMMeter.Discovery;
using LLMMeter.Persistence;
using Xunit;

namespace LLMMeter.Tests;

public class ConfigParsingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ConfigParsingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "llmmeter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "LLMMeter.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Missing_File_Yields_Defaults_And_No_Error()
    {
        var svc = new ConfigurationService(_path);
        var cfg = svc.Load();

        Assert.Null(svc.LastLoadError);
        Assert.False(svc.Exists);
        Assert.Empty(cfg.ManualBackends);
        Assert.Equal([8000, 8080, 1234, 11434], cfg.Discovery.KnownPorts);
        Assert.True(cfg.Discovery.Enabled);
    }

    [Fact]
    public void Valid_Config_Roundtrips()
    {
        var writeSvc = new ConfigurationService(_path);
        var original = new AppConfiguration();
        original.ManualBackends.Add(new ManualEndpointConfig
        {
            Name = "lab",
            Url = "http://192.168.1.31:8000",
            Type = "Auto",
        });
        original.Windows.Add(new WindowConfig
        {
            BackendId = "manual|192.168.1.31:8000",
            X = 120, Y = 80, Scale = 1.37, RequestListHeight = 222, Expanded = true, Topmost = true,
        });
        writeSvc.Save(original);

        var svc2 = new ConfigurationService(_path);
        var loaded = svc2.Load();

        Assert.Null(svc2.LastLoadError);

        var m = Assert.Single(loaded.ManualBackends);
        Assert.Equal("lab", m.Name);
        Assert.Equal("http://192.168.1.31:8000", m.Url);

        var w = Assert.Single(loaded.Windows);
        Assert.Equal(1.37, w.Scale, 3);
        Assert.Equal(222, w.RequestListHeight);
        Assert.True(w.Expanded);
        Assert.True(w.Topmost);
    }

    [Fact]
    public void Missing_Fields_Fall_Back_To_Safe_Values()
    {
        File.WriteAllText(_path, """{ "version": 1 }""");
        var cfg = new ConfigurationService(_path).Load();

        Assert.NotNull(cfg.ManualBackends);
        Assert.NotNull(cfg.Windows);
        Assert.NotNull(cfg.Discovery);
        Assert.NotEmpty(cfg.Discovery.KnownPorts);
    }

    [Fact]
    public void Old_Config_Version_Is_Normalized_Not_Rejected()
    {
        // Simulate a config written by an older schema: no windows/discovery keys.
        File.WriteAllText(_path,
            """
            { "version": 0, "manualBackends": [ { "url": "http://10.0.0.5:8000", "type": "Vllm" } ] }
            """);

        var svc = new ConfigurationService(_path);
        var cfg = svc.Load();

        Assert.Null(svc.LastLoadError);
        Assert.Equal(AppConfiguration.CurrentVersion, cfg.Version);
        Assert.Single(cfg.ManualBackends);
        Assert.NotNull(cfg.Windows);
        Assert.NotNull(cfg.Discovery);
    }

    [Fact]
    public void Corrupted_Config_Is_Backed_Up_And_Defaults_Used()
    {
        File.WriteAllText(_path, "{ this is not json !!!");
        var svc = new ConfigurationService(_path);
        var cfg = svc.Load();

        Assert.NotNull(svc.LastLoadError);           // user gets a warning hook
        Assert.Equal(_path + ".broken", svc.BackupPath);
        Assert.True(File.Exists(svc.BackupPath));    // broken file preserved
        Assert.Empty(cfg.ManualBackends);            // safe defaults
    }

    [Fact]
    public void Save_Is_Atomic_No_Tmp_Leftover()
    {
        var svc = new ConfigurationService(_path);
        svc.Save(new AppConfiguration());
        svc.Save(new AppConfiguration());

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Concurrent_Saves_Are_Serialized()
    {
        var svc = new ConfigurationService(_path);
        Parallel.For(0, 20, i => svc.Save(new AppConfiguration
        {
            TopmostByDefault = i % 2 == 0,
        }));

        var loaded = new ConfigurationService(_path).Load();
        Assert.NotNull(loaded.Discovery);
        Assert.False(File.Exists(_path + ".tmp"));
    }
}

public class DiscoveryConcurrencyTests
{
    [Fact]
    public async Task TriggerScan_Is_Single_Flight()
    {
        int calls = 0;
        var release = new TaskCompletionSource<IReadOnlyList<DiscoveredServer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var discovery = new DiscoveryService(new DiscoveryConfig(), _ =>
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        });
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.Updated += _ => updated.TrySetResult();

        discovery.TriggerScan();
        discovery.TriggerScan();
        await Task.Delay(25);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.SetResult([]);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref calls));
    }
}

public class ProcNetParserTests
{
    [Fact]
    public void Parses_Tcp_Listeners()
    {
        const string content = """
             sl local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode
                0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 12345 1
                1: 00000000:04D2 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 12346 1
                2: 0100007F:0035 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 12347 1
                3: 0100007F:C350 8EFAC110:9C41 01 00000000:00000000 00:00000000 00000000     0        0 12348 1
            """;

        var listeners = ProcNetParser.ParseListeners(content);

        Assert.Equal(3, listeners.Count);
        Assert.Contains(listeners, l => l.Port == 0x1F90 && l.LoopbackOrWildcard); // 8080 on 127.0.0.1
        Assert.Contains(listeners, l => l.Port == 0x04D2 && l.LoopbackOrWildcard); // 1234 on 0.0.0.0
        Assert.DoesNotContain(listeners, l => !l.LoopbackOrWildcard);
        // established connection (st=01) excluded
        Assert.DoesNotContain(listeners, l => l.Port == 0xC350);
    }

    [Fact]
    public void Parses_Tcp6_Loopback_And_Wildcard()
    {
        const string content = """
             sl local_address rem_address st
                0: 00000000000000000000000001000000:2CBC 00000000000000000000000000000000:0000 0A
                1: 00000000000000000000000000000000:1F90 00000000000000000000000000000000:0000 0A
                2: 00000000000000000000000000000000:9999 00000000000000000000000000000000:0000 07
            """;

        var listeners = ProcNetParser.ParseListeners(content);
        Assert.Equal(2, listeners.Count);
        Assert.Contains(listeners, l => l.Port == 0x2CBC); // ::1
        Assert.Contains(listeners, l => l.Port == 0x1F90); // ::
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage with no table header")]
    public void Tolerates_Garbage(string? content)
    {
        Assert.Empty(ProcNetParser.ParseListeners(content!));
    }
}
