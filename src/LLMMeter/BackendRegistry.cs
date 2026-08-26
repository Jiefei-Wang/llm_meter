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
            _discovery.AddKnownEndpoint(id);

        _discovery.ScanCompleted += scanResult =>
        {
            bool changed = MergeDiscovered(scanResult);
            if (changed) TargetsChanged?.Invoke();
        };

        _discovery.Updated += servers =>
        {
            bool changed = MergeDiscovered(servers);
            if (changed) TargetsChanged?.Invoke();
        };
    }

    public DiscoveryService Discovery => _discovery;

    private readonly Dictionary<string, DiscoveredServer> _discovered = new();
    private readonly Dictionary<string, int> _endpointMissCount = new(StringComparer.OrdinalIgnoreCase);

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
        "ninfer" => BackendKind.NInfer,
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
            var fp = await _fingerprinter.FingerprintAsync(uri, e.PlainTextApiKey, CancellationToken.None).ConfigureAwait(false);
            kind = fp.Kind == BackendKind.Unknown ? BackendKind.GenericOpenAi : fp.Kind;
        }
        lock (_lock) _manualKinds[e.Url] = kind;

        string normKey = Uri.TryCreate(e.Url.Trim(), UriKind.Absolute, out var parsedU)
            ? EndpointRef.NormalizeEndpointKey(parsedU)
            : e.Url.Trim();

        lock (_lock)
        {
            _config.ManualBackends.RemoveAll(m =>
                m.Url.Equals(e.Url.Trim(), StringComparison.OrdinalIgnoreCase) ||
                (Uri.TryCreate(m.Url.Trim(), UriKind.Absolute, out var mu) &&
                 EndpointRef.NormalizeEndpointKey(mu).Equals(normKey, StringComparison.OrdinalIgnoreCase)));
            _config.ManualBackends.Add(new ManualEndpointConfig
            {
                Name = e.Name,
                Url = e.Url.Trim(),
                Type = e.Type,
                ApiKey = e.ApiKey,
            });
            try { _configService.Save(_config); }
            catch (Exception ex)
            {
                Log.Warn($"failed saving config: {ex.Message}");
            }
        }

        var endpoint = MakeEndpointForManual(e);
        _discovery.AddKnownEndpoint(endpoint.Id);
        _discovery.AddKnownEndpoint(endpoint.DedupeKey);
        TargetsChanged?.Invoke();
        return e;
    }

    public void RemoveManualEndpoint(string url)
    {
        string normKey = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsedUrl)
            ? EndpointRef.NormalizeEndpointKey(parsedUrl)
            : url.Trim();

        lock (_lock)
        {
            _config.ManualBackends.RemoveAll(m =>
                m.Url.Equals(url, StringComparison.OrdinalIgnoreCase) ||
                (Uri.TryCreate(m.Url.Trim(), UriKind.Absolute, out var mu) &&
                 EndpointRef.NormalizeEndpointKey(mu).Equals(normKey, StringComparison.OrdinalIgnoreCase)));
            _manualKinds.Remove(url);
            try { _configService.Save(_config); }
            catch (Exception ex) { Log.Warn($"failed saving config: {ex.Message}"); }
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
        {
            var norm = HttpService.NormalizeBase(parsedUri);
            string id = DiscoveryService.MakeId(OriginKind.Manual, null, norm);
            string dedupeKey = EndpointRef.NormalizeEndpointKey(norm);

            lock (_lock) _discovered.Remove(id);
            _discovery.RemoveKnownEndpoint(id);
            _discovery.RemoveKnownEndpoint(dedupeKey);

            // Check if any other endpoint is still using this physical server
            bool stillInUse;
            lock (_lock)
            {
                stillInUse = _config.ManualBackends.Any(m =>
                    Uri.TryCreate(m.Url, UriKind.Absolute, out var u) &&
                    EndpointRef.NormalizeEndpointKey(u).Equals(dedupeKey, StringComparison.OrdinalIgnoreCase))
                    || _discovered.Values.Any(s =>
                    EndpointRef.NormalizeEndpointKey(s.Endpoint.BaseUrl).Equals(dedupeKey, StringComparison.OrdinalIgnoreCase));
            }

            if (!stillInUse)
            {
                Collectors.Remove(dedupeKey);
            }
        }

        TargetsChanged?.Invoke();
    }

    private EndpointRef MakeEndpointForManual(ManualEndpointConfig e)
    {
        var uri = HttpService.NormalizeBase(new Uri(e.Url.Trim()));
        string id = DiscoveryService.MakeId(OriginKind.Manual, null, uri);
        return new EndpointRef(id, uri, OriginKind.Manual, null, e.PlainTextApiKey);
    }

    private IEnumerable<string> ManualEndpointIds()
    {
        foreach (var m in _config.ManualBackends)
        {
            if (Uri.TryCreate(m.Url, UriKind.Absolute, out var uri))
            {
                yield return DiscoveryService.MakeId(OriginKind.Manual, null, uri);
                yield return EndpointRef.NormalizeEndpointKey(uri);
            }
        }
    }

    // ------------------------------------------------------------ discovered

    internal bool MergeDiscovered(IReadOnlyList<DiscoveredServer> servers) =>
        MergeDiscovered(new DiscoveryScanResult(servers));

    internal bool MergeDiscovered(DiscoveryScanResult scanResult)
    {
        bool changed = false;
        var toPruneDedupeKeys = new List<string>();

        lock (_lock)
        {
            var newServerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in scanResult.Servers)
            {
                newServerIds.Add(s.Endpoint.Id);
                _endpointMissCount[s.Endpoint.Id] = 0;

                if (!_discovered.TryGetValue(s.Endpoint.Id, out var existing))
                {
                    changed = true;
                    _discovered[s.Endpoint.Id] = s;
                }
                else if (existing.Kind != s.Kind ||
                         !string.Equals(existing.Endpoint.AuthToken, s.Endpoint.AuthToken, StringComparison.Ordinal) ||
                         !string.Equals(existing.Endpoint.BaseUrl, s.Endpoint.BaseUrl) ||
                         !string.Equals(existing.Evidence, s.Evidence, StringComparison.Ordinal))
                {
                    changed = true;
                    _discovered[s.Endpoint.Id] = s;
                    if (existing.Kind != s.Kind)
                    {
                        Collectors.UpdateBackend(s.Endpoint, s.Kind);
                    }
                }
            }

            var removedIds = new List<string>();
            foreach (var kvp in _discovered)
            {
                var id = kvp.Key;
                var existing = kvp.Value;
                if (newServerIds.Contains(id)) continue;

                // Check if the discovery source was actually scanned in this pass
                bool sourceScanned = existing.Endpoint.Origin switch
                {
                    OriginKind.WindowsHost => scanResult.WindowsScanned,
                    OriginKind.Wsl => scanResult.WslScanned && (string.IsNullOrEmpty(existing.Endpoint.WslDistro) || scanResult.ScannedWslDistros.Contains(existing.Endpoint.WslDistro)),
                    _ => true,
                };

                if (!sourceScanned)
                {
                    // Source scan failed or was skipped: preserve endpoint
                    continue;
                }

                int misses = _endpointMissCount.GetValueOrDefault(id) + 1;
                _endpointMissCount[id] = misses;

                // Require at least 2 consecutive misses before removing
                if (misses >= 2)
                {
                    removedIds.Add(id);
                }
            }

            if (removedIds.Count > 0)
            {
                changed = true;
                foreach (var id in removedIds)
                {
                    var removed = _discovered[id];
                    _discovered.Remove(id);
                    _endpointMissCount.Remove(id);

                    string dedupeKey = EndpointRef.NormalizeEndpointKey(removed.Endpoint.BaseUrl);

                    // Check if dedupeKey is still needed by remaining discovered servers or manual endpoints
                    bool stillInUse =
                        _discovered.Values.Any(s => EndpointRef.NormalizeEndpointKey(s.Endpoint.BaseUrl).Equals(dedupeKey, StringComparison.OrdinalIgnoreCase)) ||
                        _config.ManualBackends.Any(m => Uri.TryCreate(m.Url, UriKind.Absolute, out var u) &&
                            EndpointRef.NormalizeEndpointKey(u).Equals(dedupeKey, StringComparison.OrdinalIgnoreCase));

                    if (!stillInUse)
                    {
                        toPruneDedupeKeys.Add(dedupeKey);
                    }
                }
            }
        }

        foreach (var dedupeKey in toPruneDedupeKeys)
        {
            Collectors.NotifyDisappeared(dedupeKey);
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

    private static bool IsRouterMode(BackendKind kind, MetricSnapshot? latest) =>
        kind == BackendKind.LlamaCpp &&
        latest?.Info.TryGetValue("Router", out var r) == true &&
        string.Equals(r, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the dropdown model: entries grouped by origin with status.</summary>
    public List<TargetEntry> GetTargetEntries()
    {
        var list = new List<TargetEntry>();

        List<DiscoveredServer> discovered;
        lock (_lock) discovered = [.. _discovered.Values];

        void AddEntry(DiscoveredServer s, string group)
        {
            var collector = Collectors.TryGet(s.Endpoint);
            var latest = collector?.Latest;
            bool isRouter = IsRouterMode(s.Kind, latest);

            if (isRouter)
            {
                var loaded = latest?.LoadedModels ?? Array.Empty<string>();
                if (loaded.Count == 0)
                {
                    string label = DescribeTarget(s.Kind, s.Endpoint, "router (0 models)");
                    list.Add(new TargetEntry(
                        new BackendTarget(s.Endpoint.Id, s.Endpoint, s.Kind, null, label),
                        group,
                        IsOnline(latest),
                        "router",
                        latest?.State ?? ConnectionState.Connecting));
                }
                else
                {
                    foreach (var model in loaded)
                    {
                        string targetId = $"{s.Endpoint.Id}|{model}";
                        string label = DescribeTarget(s.Kind, s.Endpoint, model);
                        list.Add(new TargetEntry(
                            new BackendTarget(targetId, s.Endpoint, s.Kind, model, label),
                            group,
                            IsOnline(latest),
                            model,
                            latest?.State ?? ConnectionState.Connecting));
                    }
                }
            }
            else
            {
                list.Add(new TargetEntry(
                    new BackendTarget(
                        s.Endpoint.Id, s.Endpoint, s.Kind, null,
                        DescribeTarget(s.Kind, s.Endpoint, latest?.ModelName)),
                    group,
                    IsOnline(latest),
                    latest?.ModelName,
                    latest?.State ?? ConnectionState.Connecting));
            }
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
            var endpoint = new EndpointRef(id, norm, OriginKind.Manual, null, m.PlainTextApiKey);
            var collector = Collectors.TryGet(endpoint);
            var latest = collector?.Latest;
            var kind = LookupManualKind(m) ?? BackendKind.Unknown;
            bool isRouter = IsRouterMode(kind, latest);

            string label = string.IsNullOrWhiteSpace(m.Name)
                ? $"{norm.Host}:{norm.Port}"
                : m.Name;
            if (!string.IsNullOrWhiteSpace(m.Name)) label += $"  ({norm.Host}:{norm.Port})";

            if (isRouter)
            {
                var loaded = latest?.LoadedModels ?? Array.Empty<string>();
                if (loaded.Count == 0)
                {
                    string routerLabel = $"{label} · router";
                    list.Add(new TargetEntry(
                        new BackendTarget(id, endpoint, kind, null, routerLabel),
                        "Manual",
                        IsOnline(latest),
                        "router",
                        latest?.State ?? ConnectionState.Connecting));
                }
                else
                {
                    foreach (var model in loaded)
                    {
                        string targetId = $"{id}|{model}";
                        string modelLabel = $"{label} · {model}";
                        list.Add(new TargetEntry(
                            new BackendTarget(targetId, endpoint, kind, model, modelLabel),
                            "Manual",
                            IsOnline(latest),
                            model,
                            latest?.State ?? ConnectionState.Connecting));
                    }
                }
            }
            else
            {
                list.Add(new TargetEntry(
                    new BackendTarget(id, endpoint, kind, null, label),
                    "Manual",
                    IsOnline(latest),
                    latest?.ModelName,
                    latest?.State ?? ConnectionState.Connecting));
            }
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
