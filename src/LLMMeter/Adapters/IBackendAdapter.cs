using LLMMeter.Core;

namespace LLMMeter.Adapters;

public sealed record FingerprintResult(BackendKind Kind, string Evidence);

/// <summary>
/// A backend adapter. One instance exists per endpoint-collector and owns any
/// rate/rolling state it needs. Parsing stays here — never in the UI.
/// </summary>
public interface IBackendAdapter
{
    BackendKind Kind { get; }
    BackendCapabilities Capabilities { get; }

    /// <summary>Positively identify this endpoint as our kind. Must not throw.</summary>
    Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct);

    /// <summary>Poll everything this backend offers. Must not throw for optional failures.</summary>
    Task<MetricSnapshot> CollectAsync(IHttp http, CancellationToken ct);

    /// <summary>User-facing explanation when telemetry is limited (null = none).</summary>
    TelemetryHelp? GetHelp();
}

/// <summary>Compact "how to enable missing metrics" guidance.</summary>
public sealed record TelemetryHelp(
    string Summary,
    IReadOnlyList<string> Available,
    IReadOnlyList<string> Unavailable,
    string? SuggestedCommand,
    string? CurrentCommand);
