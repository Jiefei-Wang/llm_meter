using System.Collections.Concurrent;
using System.Diagnostics;
using LLMMeter.Core;
using LLMMeter.Persistence;

namespace LLMMeter.Discovery;

public sealed record DiscoveredServer(EndpointRef Endpoint, BackendKind Kind, string Evidence);

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

    public event Action<IReadOnlyList<DiscoveredServer>>? Updated;

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
            var result = await (_scanOverride?.Invoke(_cts.Token) ?? ScanOnceAsync(_cts.Token)).ConfigureAwait(false);
            Updated?.Invoke(result);
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

    public void AddKnownEndpoint(string id) { lock (_lock) _knownEndpointIds.Add(id); }
    public void RemoveKnownEndpoint(string id) { lock (_lock) _knownEndpointIds.Remove(id); }

    public async Task<IReadOnlyList<DiscoveredServer>> ScanOnceAsync(CancellationToken ct)
    {
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
                    // Known ports are already covered by step 1; this pass adds
                    // recognized inference processes on any other port.
                    if (pidNames.TryGetValue(l.Pid, out var name) &&
                        WindowsProcessDiscovery.IsLikelyInferenceProcess(name))
                    {
                        candidates.Add((
                            new Uri($"http://127.0.0.1:{l.Port}"),
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
                foreach (var d in wslServers)
                {
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
                // WSL absent or failing — non-fatal
            }
        }

        // Dedupe URLs, skip known endpoints
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toProbe = new List<(Uri url, string originLabel, OriginKind origin, string? distro, string hint)>();
        foreach (var c in candidates)
        {
            string key = c.origin switch
            {
                OriginKind.Wsl => $"wsl|{c.distro}|{c.url.Host}:{c.url.Port}",
                _ => $"win|{c.url.Host}:{c.url.Port}",
            };
            lock (_lock)
            {
                if (_knownEndpointIds.Contains(key)) continue;
            }
            string dedupeKey = $"{c.origin}|{c.distro}|{c.url.AbsoluteUri}";
            if (!seenUrls.Add(dedupeKey)) continue;
            toProbe.Add(c);
        }

        var results = await ProbeCandidatesAsync(toProbe, ct).ConfigureAwait(false);
        return results;
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
                    var fp = await _fingerprinter.FingerprintAsync(c.url, ct).ConfigureAwait(false);
                    if (fp.Kind != BackendKind.Unknown)
                    {
                        string id = MakeId(c.origin, c.distro, c.url);
                        var endpoint = new EndpointRef(id, c.url, c.origin, c.distro);
                        found.Add(new DiscoveredServer(endpoint, fp.Kind, $"{c.hint}; {fp.Evidence}"));
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
