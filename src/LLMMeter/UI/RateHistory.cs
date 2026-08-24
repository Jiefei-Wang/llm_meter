namespace LLMMeter.UI;

public sealed record ActivityPoint(DateTimeOffset Timestamp, double? Value);

/// <summary>Thread-safe, bounded five-minute history for the two live rate metrics.</summary>
internal sealed class RateHistory
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxSamples = 1200;
    private readonly object _lock = new();
    private readonly Queue<ActivityPoint> _prefill = new();
    private readonly Queue<ActivityPoint> _generate = new();

    public void Clear()
    {
        lock (_lock)
        {
            _prefill.Clear();
            _generate.Clear();
        }
    }

    public void Record(Core.MetricSnapshot snapshot)
    {
        lock (_lock)
        {
            Enqueue(_prefill, new ActivityPoint(snapshot.Timestamp,
                snapshot.PrefillTokPerSec.HasValue ? snapshot.PrefillTokPerSec.Value : null));
            Enqueue(_generate, new ActivityPoint(snapshot.Timestamp,
                snapshot.GenerationTokPerSec.HasValue ? snapshot.GenerationTokPerSec.Value : null));
            Prune(_prefill, snapshot.Timestamp - Window);
            Prune(_generate, snapshot.Timestamp - Window);
        }
    }

    public IReadOnlyList<ActivityPoint> PrefillSnapshot(DateTimeOffset now) => Snapshot(_prefill, now);
    public IReadOnlyList<ActivityPoint> GenerateSnapshot(DateTimeOffset now) => Snapshot(_generate, now);

    private IReadOnlyList<ActivityPoint> Snapshot(Queue<ActivityPoint> source, DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(source, now - Window);
            return source.ToArray();
        }
    }

    private static void Enqueue(Queue<ActivityPoint> queue, ActivityPoint point)
    {
        if (queue.LastOrDefault()?.Timestamp == point.Timestamp) return;
        queue.Enqueue(point);
        while (queue.Count > MaxSamples) queue.Dequeue();
    }

    private static void Prune(Queue<ActivityPoint> queue, DateTimeOffset cutoff)
    {
        while (queue.TryPeek(out var point) && point.Timestamp < cutoff) queue.Dequeue();
    }
}
