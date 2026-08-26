using LLMMeter.Core;

namespace LLMMeter.Collection;

/// <summary>
/// Central registry mapping endpoints to shared collectors. Windows never poll
/// HTTP themselves; they subscribe here.
/// </summary>
public sealed class CollectorManager : IDisposable
{
    private readonly Dictionary<string, BackendCollector> _collectors = new();
    private readonly Dictionary<string, int> _refCounts = new();
    private readonly object _lock = new();

    /// <summary>
    /// Acquires an active collector for the specified target, creating and starting it if needed,
    /// and incrementing the target's reference count.
    /// </summary>
    public BackendCollector Acquire(EndpointRef endpoint, BackendKind? knownKind, string? modelId = null)
    {
        string key = CollectorKey(endpoint, modelId);
        lock (_lock)
        {
            if (_collectors.TryGetValue(key, out var existing) && !existing.IsDisposed)
            {
                var targetKind = knownKind ?? BackendKind.Unknown;
                if (targetKind != BackendKind.Unknown && existing.EffectiveKind != targetKind)
                {
                    existing.ChangeKind(targetKind);
                }

                if (!string.Equals(existing.Endpoint.AuthToken, endpoint.AuthToken, StringComparison.Ordinal))
                {
                    existing.Reconfigure(endpoint);
                }

                _refCounts[key] = _refCounts.GetValueOrDefault(key) + 1;
                return existing;
            }

            var collector = new BackendCollector(endpoint, knownKind, modelId);
            _collectors[key] = collector;
            _refCounts[key] = 1;
            collector.Start();
            return collector;
        }
    }

    /// <summary>
    /// Decrements the reference count for the specified collector. If no observers remain,
    /// the collector is stopped, disposed, and removed.
    /// </summary>
    public void Release(BackendCollector collector)
    {
        string key = CollectorKey(collector.Endpoint, collector.ModelId);
        BackendCollector? toDispose = null;
        lock (_lock)
        {
            if (_refCounts.TryGetValue(key, out int count))
            {
                count--;
                if (count <= 0)
                {
                    _refCounts.Remove(key);
                    _collectors.Remove(key);
                    toDispose = collector;
                }
                else
                {
                    _refCounts[key] = count;
                }
            }
            else if (_collectors.TryGetValue(key, out var c) && ReferenceEquals(c, collector))
            {
                _collectors.Remove(key);
                toDispose = collector;
            }
        }
        toDispose?.Dispose();
    }

    /// <summary>
    /// Passive lookup: returns an existing active collector without creating or starting one.
    /// </summary>
    public BackendCollector? TryGet(EndpointRef endpoint, string? modelId = null)
    {
        string key = CollectorKey(endpoint, modelId);
        lock (_lock)
        {
            if (_collectors.TryGetValue(key, out var existing) && !existing.IsDisposed)
                return existing;
            return null;
        }
    }

    public BackendCollector GetOrAdd(EndpointRef endpoint, BackendKind? knownKind, string? modelId = null)
    {
        return Acquire(endpoint, knownKind, modelId);
    }

    public void UpdateBackend(EndpointRef endpoint, BackendKind newKind, string? modelId = null)
    {
        string key = CollectorKey(endpoint, modelId);
        lock (_lock)
        {
            if (_collectors.TryGetValue(key, out var existing) && !existing.IsDisposed)
            {
                existing.ChangeKind(newKind);
            }
        }
    }

    public void NotifyDisappeared(string dedupeKey)
    {
        lock (_lock)
        {
            foreach (var kvp in _collectors.ToList())
            {
                if (string.Equals(kvp.Value.Endpoint.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.StartsWith(dedupeKey + "|", StringComparison.OrdinalIgnoreCase))
                {
                    kvp.Value.MarkOffline("Endpoint disappeared from discovery");
                }
            }
        }
    }

    public static string CollectorKey(EndpointRef endpoint, string? modelId = null) =>
        $"{endpoint.DedupeKey}|{modelId ?? "*"}";

    /// <summary>
    /// Removes and disposes collectors matching the given endpoint ID, deduplication key, or prefix.
    /// Polling is stopped immediately.
    /// </summary>
    public bool Remove(string endpointIdOrKey)
    {
        List<BackendCollector> toRemove = [];
        lock (_lock)
        {
            foreach (var kvp in _collectors.ToList())
            {
                if (string.Equals(kvp.Key, endpointIdOrKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Value.Endpoint.Id, endpointIdOrKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Value.Endpoint.DedupeKey, endpointIdOrKey, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.StartsWith(endpointIdOrKey + "|", StringComparison.OrdinalIgnoreCase))
                {
                    _collectors.Remove(kvp.Key);
                    toRemove.Add(kvp.Value);
                }
            }
        }

        foreach (var c in toRemove)
        {
            c.Dispose();
        }
        return toRemove.Count > 0;
    }

    /// <summary>Drop collectors that no longer have any observers and are offline.</summary>
    public void Prune(Func<BackendCollector, bool>? keepIf)
    {
        List<string> keysToRemove = [];
        List<BackendCollector> removed = [];
        lock (_lock)
        {
            foreach (var kvp in _collectors)
            {
                if (keepIf?.Invoke(kvp.Value) == false)
                {
                    keysToRemove.Add(kvp.Key);
                    removed.Add(kvp.Value);
                }
            }
            foreach (var key in keysToRemove)
                _collectors.Remove(key);
        }
        foreach (var c in removed)
        {
            c.Dispose();
        }
    }


    public int Count
    {
        get { lock (_lock) return _collectors.Count; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _collectors.Values) c.Dispose();
            _collectors.Clear();
        }
    }
}
