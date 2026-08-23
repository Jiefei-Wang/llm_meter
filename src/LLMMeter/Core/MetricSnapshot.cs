namespace LLMMeter.Core;

public enum ConnectionState
{
    Connecting,
    Online,
    Limited,   // endpoint alive, some telemetry unavailable
    Offline,
}

/// <summary>Per-request row for the expanded view. Fields may individually be unavailable.</summary>
public sealed class RequestSnapshot
{
    public required string Id { get; init; }
    public MetricValue<long> InputTokens { get; init; } = MetricValue<long>.None;
    public MetricValue<long> OutputTokens { get; init; } = MetricValue<long>.None;
    public MetricValue<double> TokensPerSecond { get; init; } = MetricValue<double>.None;
}

/// <summary>Immutable view of everything known about a backend at a moment in time.</summary>
public sealed class MetricSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }
    public required ConnectionState State { get; init; }
    public BackendKind Kind { get; init; }

    public MetricValue<int> Running { get; init; } = MetricValue<int>.None;
    public MetricValue<int> Queued { get; init; } = MetricValue<int>.None;
    public MetricValue<double> PrefillTokPerSec { get; init; } = MetricValue<double>.None;
    public MetricValue<double> GenerationTokPerSec { get; init; } = MetricValue<double>.None;
    public MetricValue<double> KvCacheUsage { get; init; } = MetricValue<double>.None;   // 0..1
    public MetricValue<double> RecentTtftMs { get; init; } = MetricValue<double>.None;   // rolling window

    /// <summary>Cumulative output tokens generated since monitoring began.</summary>
    public MetricValue<long> GeneratedTokensTotal { get; init; } = MetricValue<long>.None;

    /// <summary>Cumulative prompt tokens prefilled since monitoring began.</summary>
    public MetricValue<long> PrefilledTokensTotal { get; init; } = MetricValue<long>.None;

    /// <summary>null => enumeration not supported at all (show "details unavailable").</summary>
    public IReadOnlyList<RequestSnapshot>? Requests { get; init; }

    public string? ModelName { get; init; }
    public IReadOnlyList<string> LoadedModels { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Info { get; init; } =
        new Dictionary<string, string>();

    public static MetricSnapshot Offline(BackendKind kind) => new()
    {
        Timestamp = DateTimeOffset.Now,
        State = ConnectionState.Offline,
        Kind = kind,
        Requests = null,
    };
}
