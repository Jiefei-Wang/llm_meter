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

    public BackendCollector GetOrAdd(EndpointRef endpoint, BackendKind? knownKind)
    {
        lock (_lock)
        {
            if (_collectors.TryGetValue(endpoint.Id, out var existing))
                return existing;

            var collector = new BackendCollector(endpoint, knownKind);
            _collectors[endpoint.Id] = collector;
            collector.Start();
            return collector;
        }
    }

    /// <summary>Drop collectors that no longer have any observers and are offline.</summary>
    public void Prune(Func<BackendCollector, bool>? keepIf)
    {
        List<BackendCollector> removed = [];
        lock (_lock)
        {
            foreach (var key in _collectors.Keys)
            {
                var c = _collectors[key];
                if (keepIf?.Invoke(c) == false)
                    removed.Add(c);
            }
            foreach (var c in removed)
                _collectors.Remove(c.Endpoint.Id);
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
