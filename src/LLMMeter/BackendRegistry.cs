using LLMMeter.Adapters;
using LLMMeter.Collection;
using LLMMeter.Core;
using LLMMeter.Discovery;
using LLMMeter.Persistence;

namespace LLMMeter;

/// <summary>
/// Owns the set of known endpoints (manual + discovered), exposes selectable
/// targets grouped by origin, and guarantees one shared collector per endpoint.
/// </summary>
public sealed class BackendRegistry : IDisposable
{
    public CollectorManager Collectors { get; } = new();

    private readonly DiscoveryService _discovery;
    private readonly ConfigurationService _configService;
    private readonly AppConfiguration _config;
    private readonly EndpointFingerprinter _fingerprinter = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, BackendKind> _manualKinds = new(StringComparer.OrdinalIgnoreCase);

    public event Action? TargetsChanged;

    public BackendRegistry(AppConfiguration config, ConfigurationService configService)
    {
        _config = config;
        _configService = configService;
        _discovery = new DiscoveryService(config.Discovery);

        foreach (var id in ManualEndpointIds())
            _discovery.KnownEndpointIds.Add(id);

        _discovery.Updated += servers =>
        {
            bool changed = MergeDiscovered(servers);
            if (changed) TargetsChanged?.Invoke();
        };
    }

    public DiscoveryService Discovery => _discovery;

    private readonly Dictionary<string, DiscoveredServer> _discovered = new();

    // ---------------------------------------------------------------- manual

    public IReadOnlyList<ManualEndpointConfig> ManualEndpoints
    {
        get { lock (_lock) return [.. _config.ManualBackends]; }
    }

    public static BackendKind ParseKind(string s) => s?.ToLowerInvariant() switch
    {
        "vllm" => BackendKind.Vllm,
        "llamacpp" or "llama-server" => BackendKind.LlamaCpp,
        "lmstudio" => BackendKind.LmStudio,
        "ollama" => BackendKind.Ollama,
        "openai" => BackendKind.GenericOpenAi,
        _ => BackendKind.Unknown,
    };

    public async Task<ManualEndpointConfig?> AddManualEndpointAsync(ManualEndpointConfig e)
    {
        if (!Uri.TryCreate(e.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Enter a valid URL such as http://192.168.1.31:8000", nameof(e));

        var kind = ParseKind(e.Type);
        if (kind == BackendKind.Unknown)
        {
            var fp = await _fingerprinter.FingerprintAsync(uri, CancellationToken.None).ConfigureAwait(false);
            kind = fp.Kind == BackendKind.Unknown ? BackendKind.GenericOpenAi : fp.Kind;
        }
        lock (_lock) _manualKinds[e.Url] = kind;

        lock (_lock)
        {
            _config.ManualBackends.RemoveAll(m => m.Url.Equals(e.Url.Trim(), StringComparison.OrdinalIgnoreCase));
            _config.ManualBackends.Add(new ManualEndpointConfig
            {
                Name = e.Name,
                Url = e.Url.Trim(),
                Type = e.Type,
            });
            try { _configService.Save(_config); }
            catch (Exception ex)
            {
                Log.Warn($"failed saving config: {ex.Message}");
            }
        }

        var endpoint = MakeEndpointForManual(e);
        _discovery.KnownEndpointIds.Add(endpoint.Id);
        TargetsChanged?.Invoke();
        return e;
    }

    public void RemoveManualEndpoint(string url)
    {
        List<BackendTarget> removedTargets = [];
        lock (_lock)
        {
            _config.ManualBackends.RemoveAll(m => m.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            _manualKinds.Remove(url);
            try { _configService.Save(_config); }
            catch (Exception ex) { Log.Warn($"failed saving config: {ex.Message}"); }
        }

        string id = $"manual|{new Uri(url).Host}:{new Uri(url).Port}";
        lock (_lock) _discovered.Remove(id);
        _discovery.KnownEndpointIds.Remove(id);
        TargetsChanged?.Invoke();
    }

    private EndpointRef MakeEndpointForManual(ManualEndpointConfig e)
    {
        var uri = HttpService.NormalizeBase(new Uri(e.Url.Trim()));
        string id = DiscoveryService.MakeId(OriginKind.Manual, null, uri);
        return new EndpointRef(id, uri, OriginKind.Manual, null);
    }

    private IEnumerable<string> ManualEndpointIds()
    {
        foreach (var m in _config.ManualBackends)
        {
            if (Uri.TryCreate(m.Url, UriKind.Absolute, out var uri))
                yield return DiscoveryService.MakeId(OriginKind.Manual, null, uri);
        }
    }

    // ------------------------------------------------------------ discovered

    private bool MergeDiscovered(IReadOnlyList<DiscoveredServer> servers)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var s in servers)
            {
                if (!_discovered.ContainsKey(s.Endpoint.Id))
                    changed = true;
                _discovered[s.Endpoint.Id] = s;
            }
        }
        return changed;
    }

    // --------------------------------------------------------------- targets

    public sealed record TargetEntry(
        BackendTarget Target,
        string GroupLabel,
        bool Online,
        string? ModelName,
        ConnectionState State);

    /// <summary>Builds the dropdown model: entries grouped by origin with status.</summary>
    public List<TargetEntry> GetTargetEntries()
    {
        var list = new List<TargetEntry>();

        List<DiscoveredServer> discovered;
        lock (_lock) discovered = [.. _discovered.Values];

        void AddEntry(DiscoveredServer s, string group)
        {
            var collector = Collectors.GetOrAdd(s.Endpoint, s.Kind);
            var latest = collector.Latest;
            list.Add(new TargetEntry(
                new BackendTarget(
                    s.Endpoint.Id, s.Endpoint, s.Kind, null,
                    DescribeTarget(s.Kind, s.Endpoint, latest?.ModelName)),
                group,
                IsOnline(latest),
                latest?.ModelName,
                latest?.State ?? ConnectionState.Connecting));
        }

        foreach (var s in discovered.Where(d => d.Endpoint.Origin == OriginKind.WindowsHost))
            AddEntry(s, "Windows");

        foreach (var grp in discovered.Where(d => d.Endpoint.Origin == OriginKind.Wsl)
                     .GroupBy(d => d.Endpoint.WslDistro ?? "WSL")
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var s in grp)
                AddEntry(s, $"WSL · {grp.Key}");
        }

        // manual
        foreach (var m in ManualEndpoints)
        {
            if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var uri)) continue;
            var norm = HttpService.NormalizeBase(uri);
            string id = DiscoveryService.MakeId(OriginKind.Manual, null, norm);
            var endpoint = new EndpointRef(id, norm, OriginKind.Manual, null);
            var collector = Collectors.GetOrAdd(endpoint, LookupManualKind(m));
            var latest = collector.Latest;

            string label = string.IsNullOrWhiteSpace(m.Name)
                ? $"{norm.Host}:{norm.Port}"
                : m.Name;
            if (!string.IsNullOrWhiteSpace(m.Name)) label += $"  ({norm.Host}:{norm.Port})";

            list.Add(new TargetEntry(
                new BackendTarget(id, endpoint, collector.KnownKind ?? BackendKind.Unknown, null, label),
                "Manual",
                IsOnline(latest),
                latest?.ModelName,
                latest?.State ?? ConnectionState.Connecting));
        }

        return list;
    }

    internal static string DescribeTarget(BackendKind kind, EndpointRef e, string? modelName)
    {
        string baseName = $"{kind.DisplayName()} :{e.BaseUrl.Port}";
        if (!string.IsNullOrEmpty(modelName))
            baseName += $" · {modelName}";
        return baseName;
    }

    internal static bool IsOnline(MetricSnapshot? s) =>
        s is { State: ConnectionState.Online or ConnectionState.Limited };

    private BackendKind? LookupManualKind(ManualEndpointConfig m)
    {
        var parsed = ParseKind(m.Type);
        if (parsed != BackendKind.Unknown) return parsed;
        lock (_lock)
        {
            return _manualKinds.TryGetValue(m.Url, out var k) ? k : null; // null → fingerprint in collector
        }
    }

    public TelemetryHelp? GetHelpFor(BackendCollector collector) => collector.GetHelp();

    /// <summary>Inject recovered process command line (llama-server help popup).</summary>
    public void SetCurrentCommand(EndpointRef endpoint, string cmd)
    {
        if (Collectors.GetOrAdd(endpoint, BackendKind.LlamaCpp) is { } c)
            c.SetCurrentCommand(cmd);
    }

    public void Dispose()
    {
        _discovery.Dispose();
        Collectors.Dispose();
    }
}
