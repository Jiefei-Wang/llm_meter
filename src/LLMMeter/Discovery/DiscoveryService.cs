using System.Collections.Concurrent;
using System.Diagnostics;
using LLMMeter.Core;
using LLMMeter.Persistence;

namespace LLMMeter.Discovery;

public sealed record DiscoveredServer(EndpointRef Endpoint, BackendKind Kind, string Evidence);

public sealed class DiscoveryScanResult
{
    public IReadOnlyList<DiscoveredServer> Servers { get; }
    public bool WindowsScanned { get; }
    public bool WslScanned { get; }
    public IReadOnlySet<string> ScannedWslDistros { get; }

    public DiscoveryScanResult(
        IReadOnlyList<DiscoveredServer> servers,
        bool windowsScanned = true,
        bool wslScanned = true,
        IReadOnlySet<string>? scannedWslDistros = null)
    {
        Servers = servers;
        WindowsScanned = windowsScanned;
        WslScanned = wslScanned;
        ScannedWslDistros = scannedWslDistros ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Automatic discovery: known local ports + Windows loopback listeners +
/// running WSL distributions. Never scans the LAN. Fingerprinting decides
/// backend identity — port numbers never do.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(12);

    private readonly DiscoveryConfig _config;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiscoveredServer>>>? _scanOverride;
    private readonly EndpointFingerprinter _fingerprinter = new();
    private readonly SemaphoreSlim _probeGate = new(8, 8);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    private Timer? _timer;
    private Task? _inFlight;
    private int _disposed;

    /// <summary>Endpoints already known (manual config) — skipped in results.</summary>
    private readonly HashSet<string> _knownEndpointIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownEndpointKeys = new(StringComparer.OrdinalIgnoreCase);

    public event Action<IReadOnlyList<DiscoveredServer>>? Updated;
    public event Action<DiscoveryScanResult>? ScanCompleted;

    public DiscoveryService(DiscoveryConfig config) => _config = config;

    internal DiscoveryService(DiscoveryConfig config,
        Func<CancellationToken, Task<IReadOnlyList<DiscoveredServer>>> scanOverride)
    {
        _config = config;
        _scanOverride = scanOverride;
    }

    public void Start()
    {
        _timer ??= new Timer(_ => TriggerScan(), null, TimeSpan.Zero, RescanInterval);
    }

    public void TriggerScan()
    {
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: false }) return;
            _inFlight = RunScanAndPublishAsync();
        }
    }

    private async Task RunScanAndPublishAsync()
    {
        // Prevent synchronous completion from racing the _inFlight assignment.
        await Task.Yield();
        try
        {
            DiscoveryScanResult scanResult;
            if (_scanOverride != null)
            {
                var servers = await _scanOverride(_cts.Token).ConfigureAwait(false);
                scanResult = new DiscoveryScanResult(servers);
            }
            else
            {
                scanResult = await ScanFullOnceAsync(_cts.Token).ConfigureAwait(false);
            }
            Updated?.Invoke(scanResult.Servers);
            ScanCompleted?.Invoke(scanResult);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"discovery scan failed: {ex.GetBaseException().Message}");
        }
        finally
        {
            lock (_lock) _inFlight = null;
        }
    }

    public void AddKnownEndpoint(string id)
    {
        lock (_lock)
        {
            _knownEndpointIds.Add(id);
            if (Uri.TryCreate(id, UriKind.Absolute, out var uri))
            {
                _knownEndpointKeys.Add(EndpointRef.NormalizeEndpointKey(uri));
            }
            else if (id.Contains('|'))
            {
                var hostPort = id.Split('|')[^1];
                if (Uri.TryCreate($"http://{hostPort}", UriKind.Absolute, out var parsed))
                    _knownEndpointKeys.Add(EndpointRef.NormalizeEndpointKey(parsed));
            }
        }
    }

    public void RemoveKnownEndpoint(string id)
    {
        lock (_lock)
        {
            _knownEndpointIds.Remove(id);
            if (Uri.TryCreate(id, UriKind.Absolute, out var uri))
            {
                _knownEndpointKeys.Remove(EndpointRef.NormalizeEndpointKey(uri));
            }
            else if (id.Contains('|'))
            {
                var hostPort = id.Split('|')[^1];
                if (Uri.TryCreate($"http://{hostPort}", UriKind.Absolute, out var parsed))
                    _knownEndpointKeys.Remove(EndpointRef.NormalizeEndpointKey(parsed));
            }
        }
    }

    public async Task<IReadOnlyList<DiscoveredServer>> ScanOnceAsync(CancellationToken ct)
    {
        var scan = await ScanFullOnceAsync(ct).ConfigureAwait(false);
        return scan.Servers;
    }

    public async Task<DiscoveryScanResult> ScanFullOnceAsync(CancellationToken ct)
    {
        bool windowsScanned = true;
        bool wslScanned = false;
        var scannedDistros = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<(Uri url, string originLabel, OriginKind origin, string? distro, string evidenceHint)>();

        // 1. Known ports on 127.0.0.1
        if (_config.Enabled)
        {
            foreach (var port in _config.KnownPorts.Distinct())
                candidates.Add((new Uri($"http://127.0.0.1:{port}"), "Windows", OriginKind.WindowsHost, null, "known port"));
        }

        // 2. Windows listening processes
        if (_config.Enabled && _config.WindowsListeners)
        {
            try
            {
                var pidNames = GetProcessNameMap();
                foreach (var l in WindowsProcessDiscovery.GetLoopbackListeners())
                {
                    if (pidNames.TryGetValue(l.Pid, out var name) &&
                        WindowsProcessDiscovery.IsLikelyInferenceProcess(name))
                    {
                        string host = l.Address == "[::1]" ? "[::1]" : "127.0.0.1";
                        candidates.Add((
                            new Uri($"http://{host}:{l.Port}"),
                            "Windows", OriginKind.WindowsHost, null,
                            $"listener process: {name}.exe"));
                    }
                }
            }
            catch
            {
                // table unavailable — known-port probing still ran
            }
        }

        // 3. Running WSL distributions
        if (_config.Enabled && _config.WslEnabled)
        {
            try
            {
                var wslServers = await WslDiscovery.ScanAsync(ct).ConfigureAwait(false);
                wslScanned = true;
                foreach (var d in wslServers)
                {
                    scannedDistros.Add(d.Name);
                    foreach (var p in d.ListeningPorts.Take(24))
                    {
                        candidates.Add((
                            new Uri($"http://127.0.0.1:{p}"),
                            $"WSL · {d.Name}", OriginKind.Wsl, d.Name,
                            $"listening in WSL '{d.Name}'"));
                        if (!string.IsNullOrEmpty(d.IpAddress))
                        {
                            candidates.Add((
                                new Uri($"http://{d.IpAddress}:{p}"),
                                $"WSL · {d.Name}", OriginKind.Wsl, d.Name,
                                $"WSL IP fallback '{d.IpAddress}'"));
                        }
                    }
                }
            }
            catch
            {
                // WSL absent or failing — preserve existing WSL endpoints
                wslScanned = false;
            }
        }
        else
        {
            wslScanned = true;
        }

        // Dedupe candidates, merging hints
        var candidateMap = new Dictionary<string, (Uri url, string originLabel, OriginKind origin, string? distro, string hint)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            string key = c.origin switch
            {
                OriginKind.Wsl => $"wsl|{c.distro}|{c.url.Host}:{c.url.Port}",
                _ => $"win|{c.url.Host}:{c.url.Port}",
            };
            string normKey = EndpointRef.NormalizeEndpointKey(c.url);
            lock (_lock)
            {
                if (_knownEndpointIds.Contains(key)) continue;
                if (c.origin == OriginKind.WindowsHost && _knownEndpointKeys.Contains(normKey)) continue;
            }
            string dedupeKey = $"{c.origin}|{c.distro}|{normKey}";
            if (candidateMap.TryGetValue(dedupeKey, out var existing))
            {
                string mergedHint;
                if (c.evidenceHint.Contains("listener process:", StringComparison.OrdinalIgnoreCase))
                    mergedHint = existing.hint.Contains("listener process:", StringComparison.OrdinalIgnoreCase)
                        ? $"{existing.hint}; {c.evidenceHint}"
                        : c.evidenceHint;
                else if (existing.hint.Contains("listener process:", StringComparison.OrdinalIgnoreCase))
                    mergedHint = existing.hint;
                else
                    mergedHint = $"{existing.hint}; {c.evidenceHint}";

                candidateMap[dedupeKey] = (existing.url, existing.originLabel, existing.origin, existing.distro, mergedHint);
            }
            else
            {
                candidateMap[dedupeKey] = (c.url, c.originLabel, c.origin, c.distro, c.evidenceHint);
            }
        }

        var toProbe = candidateMap.Values.ToList();
        var results = await ProbeCandidatesAsync(toProbe, ct).ConfigureAwait(false);
        return new DiscoveryScanResult(results, windowsScanned, wslScanned, scannedDistros);
    }

    private async Task<List<DiscoveredServer>> ProbeCandidatesAsync(
        List<(Uri url, string originLabel, OriginKind origin, string? distro, string hint)> toProbe,
        CancellationToken ct)
    {
        var found = new ConcurrentBag<DiscoveredServer>();
        var tasks = toProbe.Select(async c =>
        {
            try
            {
                await _probeGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    string id = MakeId(c.origin, c.distro, c.url);
                    var endpoint = new EndpointRef(id, c.url, c.origin, c.distro);
                    var fp = await _fingerprinter.FingerprintAsync(endpoint, ct).ConfigureAwait(false);
                    var kind = fp.Kind;
                    if ((kind == BackendKind.GenericOpenAi || kind == BackendKind.Unknown) &&
                        (c.hint.Contains("ninfer-serve", StringComparison.OrdinalIgnoreCase) ||
                         c.hint.Contains("ninfer", StringComparison.OrdinalIgnoreCase)))
                    {
                        kind = BackendKind.NInfer;
                    }
                    if (kind != BackendKind.Unknown)
                    {
                        found.Add(new DiscoveredServer(endpoint, kind, $"{c.hint}; {fp.Evidence}"));
                    }

                }
                finally
                {
                    _probeGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch { /* single probe failure ignored */ }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. found.OrderBy(f => f.Endpoint.BaseUrl.Port)];
    }

    public static string MakeId(OriginKind origin, string? distro, Uri url) => origin switch
    {
        OriginKind.Wsl => $"wsl|{distro}|{url.Host}:{url.Port}",
        OriginKind.Manual => $"manual|{url.Host}:{url.Port}",
        _ => $"win|{url.Host}:{url.Port}",
    };

    private static Dictionary<int, string> GetProcessNameMap()
    {
        var map = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = p.ProcessName; }
            catch { /* exited */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _timer?.Dispose();
        Task? scan;
        lock (_lock) scan = _inFlight;
        if (scan is null or { IsCompleted: true })
            Cleanup();
        else
            _ = scan.ContinueWith(_ => Cleanup(), TaskScheduler.Default);

        void Cleanup()
        {
            _probeGate.Dispose();
            _cts.Dispose();
        }
    }
}
