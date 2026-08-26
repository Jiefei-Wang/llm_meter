using LLMMeter.Core;

namespace LLMMeter.Collection;

/// <summary>
/// Central registry mapping endpoints to shared collectors. Windows never poll
/// HTTP themselves; they subscribe here.
/// </summary>
public sealed class CollectorManager : IDisposable
{
    private readonly Dictionary<string, BackendCollector> _collectors = new();
    private readonly object _lock = new();

    public BackendCollector GetOrAdd(EndpointRef endpoint, BackendKind? knownKind, string? modelId = null)
    {
        string key = CollectorKey(endpoint, modelId);
        lock (_lock)
        {
            if (_collectors.TryGetValue(key, out var existing))
                return existing;

            var collector = new BackendCollector(endpoint, knownKind, modelId);
            _collectors[key] = collector;
            collector.Start();
            return collector;
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
        List<BackendCollector> removed = [];
        lock (_lock)
        {
            foreach (var key in _collectors.Keys.ToList())
            {
                var c = _collectors[key];
                if (keepIf?.Invoke(c) == false)
                    removed.Add(c);
            }
            foreach (var c in removed)
                _collectors.Remove(CollectorKey(c.Endpoint));
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
