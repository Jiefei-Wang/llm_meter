using LLMMeter.Collection;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// vLLM adapter: parses Prometheus /metrics with alias tolerance across versions.
/// </summary>
public sealed class VllmAdapter : IBackendAdapter
{
    public BackendKind Kind => BackendKind.Vllm;

    public BackendCapabilities Capabilities =>
        BackendCapabilities.RunningRequests
        | BackendCapabilities.QueuedRequests
        | BackendCapabilities.AggregatePrefillRate
        | BackendCapabilities.AggregateGenerationRate
        | BackendCapabilities.KvCacheUsage
        | BackendCapabilities.RecentRequestTtft;

    private readonly RateCalculator _prefill = new();
    private readonly RateCalculator _gen = new();
    private readonly RollingTtft _ttft = new(10);
    private string? _modelName;

    // Metric name aliases (vLLM renamed several metrics between versions).
    internal static readonly string[] RunningNames = ["vllm:num_requests_running"];
    internal static readonly string[] WaitingNames = ["vllm:num_requests_waiting", "vllm:num_requests_swapped"];
    internal static readonly string[] PrefillCounterNames = ["vllm:prompt_tokens_total", "vllm:prompt_tokens_count_total"];
    internal static readonly string[] GenCounterNames = ["vllm:generation_tokens_total", "vllm:generation_tokens_count_total"];
    internal static readonly string[] KvUsageNames = ["vllm:kv_cache_usage_perc", "vllm:gpu_cache_usage_perc", "vllm:cpu_gpu_cache_usage_perc"];
    internal static readonly string[] TtftSumNames = ["vllm:time_to_first_token_seconds_sum"];
    internal static readonly string[] TtftCountNames = ["vllm:time_to_first_token_seconds_count"];

    public async Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct)
    {
        var (status, body) = await http.GetStringAsync("metrics", ct).ConfigureAwait(false);
        if (status != 200) return null;
        if (body.Contains("vllm:", StringComparison.Ordinal))
            return new FingerprintResult(Kind, "/metrics contains vllm:* families");
        return null;
    }

    public async Task<MetricSnapshot> CollectAsync(IHttp http, CancellationToken ct)
    {
        var (status, body) = await http.GetStringAsync("metrics", ct).ConfigureAwait(false);
        if (status != 200 || string.IsNullOrEmpty(body))
            return Offline();

        List<PromSample> samples;
        try { samples = PrometheusParser.Parse(body); }
        catch { return Offline(); }

        double? Sum(string[] names)
        {
            foreach (var n in names)
            {
                double sum = 0; bool any = false;
                foreach (var s in samples)
                    if (s.Name == n) { sum += s.Value; any = true; }
                if (any) return sum;
            }
            return null;
        }

        double? Average(string[] names)
        {
            foreach (var n in names)
            {
                double sum = 0; int count = 0;
                foreach (var s in samples)
                    if (s.Name == n) { sum += s.Value; count++; }
                if (count > 0) return sum / count;
            }
            return null;
        }

        _modelName ??= samples.FirstOrDefault(s => s.TryGetLabel("model_name", out _)) is { } ms &&
                       ms.TryGetLabel("model_name", out var mn) ? mn : null;

        var now = MonoClock.NowTicks;

        var runningV = Sum(RunningNames);
        var waitingV = Sum(WaitingNames);
        var kvV = Average(KvUsageNames);
        var prefillC = Sum(PrefillCounterNames);
        var genC = Sum(GenCounterNames);

        long? ttftCount = null; double? ttftSum = null;
        if (Sum(TtftCountNames) is { } tc && Sum(TtftSumNames) is { } ts)
        {
            ttftCount = (long)tc;
            ttftSum = ts;
        }
        if (ttftCount.HasValue && ttftSum.HasValue)
            _ttft.Observe(ttftCount.Value, ttftSum.Value, now);

        var prefillRate = prefillC.HasValue ? _prefill.Update(prefillC.Value, now) : MetricValue<double>.None;
        var genRate = genC.HasValue ? _gen.Update(genC.Value, now) : MetricValue<double>.None;

        var running = runningV.HasValue ? MetricValue<int>.Exact((int)Math.Round(runningV.Value), MetricSource.NativeMetrics, "/metrics gauge") : MetricValue<int>.None;
        var queued = waitingV.HasValue ? MetricValue<int>.Exact((int)Math.Round(waitingV.Value), MetricSource.NativeMetrics, "/metrics gauge") : MetricValue<int>.None;

        var kv = kvV.HasValue ? MetricValue<double>.Exact(Math.Clamp(kvV.Value, 0, 1), MetricSource.NativeMetrics) : MetricValue<double>.None;

        var ttftAvg = _ttft.AverageSeconds();
        MetricValue<double> ttft = ttftAvg.HasValue
            ? (_ttft.IsExactEstimate()
                ? MetricValue<double>.Exact(ttftAvg.Value * 1000.0, MetricSource.Derived, "rolling last-10 from TTFT histogram deltas")
                : MetricValue<double>.Approx(ttftAvg.Value * 1000.0, MetricSource.Derived,
                    _ttft.TotalSamples < 10
                        ? $"rolling estimate ({_ttft.TotalSamples} requests) from TTFT histogram deltas"
                        : "rolling estimate from TTFT histogram deltas"))
            : MetricValue<double>.None;

        var state = ComputeState(running, queued, prefillRate, genRate, kv, ttft);
        return new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = state,
            Kind = Kind,
            Running = running,
            Queued = queued,
            PrefillTokPerSec = prefillRate,
            GenerationTokPerSec = genRate,
            KvCacheUsage = kv,
            RecentTtftMs = ttft,
            GeneratedTokensTotal = genC.HasValue
                ? MetricValue<long>.Exact((long)genC.Value, MetricSource.NativeMetrics, "vllm generation token counter")
                : MetricValue<long>.None,
            PrefilledTokensTotal = prefillC.HasValue
                ? MetricValue<long>.Exact((long)prefillC.Value, MetricSource.NativeMetrics, "vllm prompt token counter")
                : MetricValue<long>.None,
            Requests = null, // /metrics does not enumerate active requests (see spec §27)
            ModelName = _modelName,
            Info = new Dictionary<string, string> { ["Source"] = "/metrics (Prometheus)" },
        };
    }

    private static ConnectionState ComputeState(
        MetricValue<int> r, MetricValue<int> q,
        MetricValue<double> p, MetricValue<double> g,
        MetricValue<double> kv, MetricValue<double> ttft) =>
        r.HasValue && q.HasValue && p.HasValue && g.HasValue
            ? ConnectionState.Online
            : ConnectionState.Limited;

    private MetricSnapshot Offline() => MetricSnapshot.Offline(Kind);

    public TelemetryHelp? GetHelp() => null; // vLLM /metrics provides the full core telemetry set
}
