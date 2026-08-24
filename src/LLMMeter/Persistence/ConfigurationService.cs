using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMMeter.Persistence;

public sealed class ManualEndpointConfig
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";       // full base URL, e.g. http://192.168.1.31:8000
    public string Type { get; set; } = "Auto";  // Auto|Vllm|LlamaCpp|LmStudio|Ollama|OpenAi
}

public sealed class WindowConfig
{
    public string BackendId { get; set; } = "";   // endpoint id or target id
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Scale { get; set; } = 1.0;
    public double RequestListHeight { get; set; }
    public bool Expanded { get; set; }
    public bool Topmost { get; set; }
    public bool Visible { get; set; } = true;
}

public sealed class AppConfiguration
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("manualBackends")]
    public List<ManualEndpointConfig> ManualBackends { get; set; } = [];

    [JsonPropertyName("windows")]
    public List<WindowConfig> Windows { get; set; } = [];

    [JsonPropertyName("discovery")]
    public DiscoveryConfig Discovery { get; set; } = new();

    /// <summary>Newly created widgets default to always-on-top.</summary>
    [JsonPropertyName("topmostByDefault")]
    public bool TopmostByDefault { get; set; } = true;
}

public sealed class DiscoveryConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("knownPorts")]
    public List<int> KnownPorts { get; set; } = [8000, 8080, 1234, 11434];

    [JsonPropertyName("wslEnabled")]
    public bool WslEnabled { get; set; } = true;

    [JsonPropertyName("windowsListeners")]
    public bool WindowsListeners { get; set; } = true;
}

/// <summary>
/// Loads/saves LLMMeter.json beside the EXE. Atomic writes, corruption-safe loads.
/// </summary>
public sealed class ConfigurationService
{
    private readonly object _saveLock = new();
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public string ConfigPath { get; }
    public string? LastLoadError { get; private set; }
    public string? BackupPath { get; private set; }

    public ConfigurationService(string? path = null)
    {
        var exeDir = AppContext.BaseDirectory;
        ConfigPath = path ?? Path.Combine(exeDir, "LLMMeter.json");
    }

    public bool Exists => File.Exists(ConfigPath);

    /// <summary>Load config; never throws. Corrupt files are backed up and defaults returned.</summary>
    public AppConfiguration Load()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfiguration();

            var json = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(json))
                return new AppConfiguration();

            var cfg = JsonSerializer.Deserialize<AppConfiguration>(json, ReadOptions);
            if (cfg is null)
                throw new InvalidDataException("configuration parsed to null");

            Normalize(cfg);
            return cfg;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or NotSupportedException)
        {
            LastLoadError = ex.Message;

            // Preserve the broken file so nothing is silently destroyed.
            try
            {
                BackupPath = ConfigPath + ".broken";
                File.Copy(ConfigPath, BackupPath, overwrite: true);
            }
            catch
            {
                BackupPath = null;
            }

            return new AppConfiguration();
        }
    }

    internal static void Normalize(AppConfiguration cfg)
    {
            // Old/unknown versions: keep what we can parse, reset version marker.
            cfg.Version = AppConfiguration.CurrentVersion;
        cfg.ManualBackends ??= [];
        cfg.Windows ??= [];
        cfg.Discovery ??= new DiscoveryConfig();
        cfg.Discovery.KnownPorts ??= [8000, 8080, 1234, 11434];
    }

    /// <summary>Atomic save: temp file + flush + replace.</summary>
    public void Save(AppConfiguration cfg)
    {
        lock (_saveLock) SaveCore(cfg);
    }

    private void SaveCore(AppConfiguration cfg)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = ConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(cfg, WriteOptions);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sw = new StreamWriter(fs))
        {
            sw.Write(json);
            sw.Flush();
            fs.Flush(flushToDisk: true);
        }

        // File.Replace requires an existing destination; first save is a plain move.
        if (File.Exists(ConfigPath))
            File.Replace(tmp, ConfigPath, destinationBackupFileName: null);
        else
            File.Move(tmp, ConfigPath);
    }
}
