using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// NInfer backend adapter.
/// Supports two modes:
/// 1. Limited mode: passive HTTP monitoring only (/health, /v1/models).
/// 2. Full mode: automatically consumes native --request-log-jsonl structured telemetry.
/// </summary>
public sealed class NInferAdapter : IBackendAdapter, IDisposable
{
    public BackendKind Kind => BackendKind.NInfer;

    private readonly EndpointRef? _endpoint;
    private readonly NInferJsonlTelemetryReader _reader;
    private readonly bool _ownsReader;

    private string? _currentCommand;
    private string? _httpModelName;
    private List<string> _loadedModels = [];
    private long _lastModelProbeTicks;
    private bool _isFullTelemetryActive;

    internal Func<long> Clock = () => MonoClock.NowTicks;

    public NInferAdapter(EndpointRef? endpoint = null)
        : this(endpoint, new NInferJsonlTelemetryReader(), ownsReader: true)
    {
    }

    internal NInferAdapter(EndpointRef? endpoint, NInferJsonlTelemetryReader reader, bool ownsReader = false)
    {
        _endpoint = endpoint;
        _reader = reader;
        _ownsReader = ownsReader;

        if (_endpoint != null)
        {
            string hostPath = NInferPathHelper.ResolveHostTelemetryPath(_endpoint);
            _reader.FilePath = hostPath;
        }
    }

    public BackendCapabilities Capabilities => _isFullTelemetryActive
        ? BackendCapabilities.RunningRequests
          | BackendCapabilities.QueuedRequests
          | BackendCapabilities.AggregatePrefillRate
          | BackendCapabilities.AggregateGenerationRate
          | BackendCapabilities.RecentRequestTtft
          | BackendCapabilities.ActiveRequestEnumeration
        : BackendCapabilities.None;

    public void SetCurrentCommand(string cmd) => _currentCommand = cmd;

    public Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct)
    {
        // 1. If the deterministic telemetry file exists for this port, that is a positive NInfer fingerprint
        if (_endpoint != null)
        {
            string hostPath = NInferPathHelper.ResolveHostTelemetryPath(_endpoint);
            if (File.Exists(hostPath))
            {
                return Task.FromResult<FingerprintResult?>(new FingerprintResult(Kind, $"found NInfer telemetry log at {hostPath}"));
            }
        }

        // 2. HTTP health check: upstream NInfer /health returns {"status":"ok"}
        // Note: As specified in Part A2, /health or /v1/models alone without process identity or
        // explicit manual configuration does not uniquely distinguish NInfer from GenericOpenAi.
        // Process discovery or explicit configuration supplies positive identification.
        return Task.FromResult<FingerprintResult?>(null);
    }


    public async Task<MetricSnapshot> CollectAsync(IHttp http, CancellationToken ct)
    {
        long now = Clock();

        // 1. HTTP health check
        var health = await http.GetJsonAsync("health", ct).ConfigureAwait(false);
        bool healthOk = health.HasValue && health.Value.ValueKind == JsonValueKind.Object &&
                        health.Value.TryGetProperty("status", out var st) &&
                        st.GetString() == "ok";

        // Probe /v1/models periodically (~5s) or if health didn't respond
        if (!healthOk || _httpModelName == null || now - _lastModelProbeTicks >= Stopwatch.Frequency * 5)
        {
            _lastModelProbeTicks = now;
            var models = await http.GetJsonAsync("v1/models", ct).ConfigureAwait(false);
            if (models.HasValue && models.Value.ValueKind == JsonValueKind.Object &&
                models.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    {
                        list.Add(idEl.GetString()!);
                    }
                }
                if (list.Count > 0)
                {
                    _loadedModels = list;
                    _httpModelName = list[0];
                    healthOk = true;
                }
            }
        }

        if (!healthOk)
        {
            _isFullTelemetryActive = false;
            return MetricSnapshot.Offline(Kind);
        }

        // 2. Check telemetry JSONL log
        if (_endpoint != null && string.IsNullOrEmpty(_reader.FilePath))
        {
            _reader.FilePath = NInferPathHelper.ResolveHostTelemetryPath(_endpoint);
        }

        bool hasTelemetry = _reader.Poll(now);
        var builder = new MetricSnapshotBuilder
        {
            Kind = Kind,
            ModelName = _reader.PublicModelId ?? _httpModelName,
            LoadedModels = _loadedModels.Count > 0 ? _loadedModels : (_reader.PublicModelId != null ? [_reader.PublicModelId] : Array.Empty<string>()),
        };

        if (hasTelemetry)
        {
            // Check for stale telemetry (e.g. > 30 seconds or 4x stats interval without events)
            double statsInterval = Math.Max(5.0, _reader.LastIntervalSeconds);
            double staleThresholdSeconds = Math.Max(30.0, statsInterval * 4);
            long elapsedTicks = now - _reader.LastEventTicks;
            bool isStale = _reader.LastEventTicks > 0 && elapsedTicks > Stopwatch.Frequency * staleThresholdSeconds;

            if (isStale)
            {
                _isFullTelemetryActive = false;
                builder.State = ConnectionState.Limited;
                builder.Info["Telemetry"] = "NInfer telemetry log is stale";
                if (!string.IsNullOrEmpty(_reader.PublicModelId))
                    builder.ModelName = _reader.PublicModelId;
            }
            else
            {
                _isFullTelemetryActive = true;
                builder.State = ConnectionState.Online;
                _reader.PopulateSnapshot(builder);
            }
        }
        else
        {
            _isFullTelemetryActive = false;
            builder.State = ConnectionState.Limited;
            builder.Info["State"] = "Limited";
        }

        return builder.Build();
    }

    public TelemetryHelp? GetHelp()
    {
        if (_isFullTelemetryActive) return null;

        int port = _endpoint?.BaseUrl.Port ?? 8080;
        string linuxPath = NInferPathHelper.BuildNInferLinuxTelemetryPath(port);

        string suggestedCmd;
        if (!string.IsNullOrWhiteSpace(_currentCommand))
        {
            string flag = $"--request-log-jsonl {linuxPath}";
            suggestedCmd = _currentCommand.Contains("--request-log-jsonl", StringComparison.Ordinal)
                ? _currentCommand
                : $"{_currentCommand.TrimEnd()} {flag}";
        }
        else
        {
            suggestedCmd = $"mkdir -p /tmp/llmmeter && ninfer-serve ... --port {port} --request-log-jsonl {linuxPath}";
        }

        return new TelemetryHelp(
            "Full NInfer telemetry is available through NInfer's native --request-log-jsonl option.",
            ["Online/Offline status", "Model list"],
            ["Running requests", "Queued requests", "Prefill throughput", "Generation throughput", "Recent TTFT", "Active requests"],
            suggestedCmd,
            _currentCommand);
    }

    public void Dispose()
    {
        if (_ownsReader)
        {
            _reader.Dispose();
        }
    }
}
