using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LLMMeter.Collection;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// llama.cpp llama-server adapter. Uses Prometheus /metrics when enabled and
/// falls back to /slots (metrics-only JSON) for what can be defensibly derived.
/// </summary>
public sealed class LlamaCppAdapter : IBackendAdapter
{
    public BackendKind Kind => BackendKind.LlamaCpp;

    private readonly RateCalculator _prefill = new();
    private readonly RateCalculator _gen = new();
    private readonly SlotTracker _slots = new();
    // /metrics state
    private bool? _metricsEnabled;
    private long _lastTicks;

    // /props state
    private int? _totalSlots;
    private string? _modelPath;

    // Cumulative generated tokens (since monitoring began). We sum positive
    // n_decoded increments per slot; a reset between tasks never subtracts.
    private readonly Dictionary<int, long> _slotLastDecoded = new();
    private long _generatedTotal;

    // Cumulative prefilled prompt tokens (since monitoring began).
    private readonly Dictionary<int, long> _slotLastProcessed = new();
    private long _prefilledTotal;

    internal static readonly string[] ProcessingNames = ["llamacpp:requests_processing"];
    internal static readonly string[] DeferredNames = ["llamacpp:requests_deferred"];
    internal static readonly string[] PrefillCounterNames = ["llamacpp:prompt_tokens_total"];
    internal static readonly string[] GenCounterNames = ["llamacpp:tokens_predicted_total"];

    public BackendCapabilities Capabilities =>
        BackendCapabilities.RunningRequests                       // both modes
        | BackendCapabilities.AggregateGenerationRate             // counters or slot deltas
        | (_metricsEnabled == true
            ? BackendCapabilities.QueuedRequests
              | BackendCapabilities.AggregatePrefillRate
              | BackendCapabilities.RecentRequestTtft
            : 0)
        | (_metricsEnabled == false
            ? BackendCapabilities.ActiveRequestEnumeration
              | BackendCapabilities.PerRequestInputTokens
              | BackendCapabilities.PerRequestOutputTokens
              | BackendCapabilities.PerRequestGenerationRate
            : 0);

    public async Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct)
    {
        var (status, body) = await http.GetStringAsync("metrics", ct).ConfigureAwait(false);
        if (status == 200 && body.Contains("llamacpp:", StringComparison.Ordinal))
            return new FingerprintResult(Kind, "/metrics contains llamacpp:* families");

        var slots = await http.GetJsonAsync("slots", ct).ConfigureAwait(false);
        if (slots.HasValue && LooksLikeSlots(slots.Value))
            return new FingerprintResult(Kind, "/slots returns llama.cpp slot structures");

        return null;
    }

    internal static bool LooksLikeSlots(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0) return false;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            return item.TryGetProperty("id", out _) &&
                   (item.TryGetProperty("is_processing", out _) ||
                    item.TryGetProperty("n_decoded", out _) ||
                    (item.TryGetProperty("next_token", out var nt) && nt.ValueKind == JsonValueKind.Array));
        }
        return false;
    }

    public async Task<MetricSnapshot> CollectAsync(IHttp http, CancellationToken ct)
    {
        var now = MonoClock.NowTicks;
        var info = new Dictionary<string, string>();

        // --- /props occasionally for metadata
        if ((_modelPath is null || _totalSlots is null) && now - _lastTicks > Stopwatch.Frequency * 5)
        {
            var props = await http.GetJsonAsync("props", ct).ConfigureAwait(false);
            if (props.HasValue && props.Value.ValueKind == JsonValueKind.Object)
            {
                if (props.Value.TryGetProperty("total_slots", out var ts) && ts.ValueKind == JsonValueKind.Number)
                    _totalSlots = ts.GetInt32();
                if (props.Value.TryGetProperty("default_generation_settings", out var dgs) &&
                    dgs.ValueKind == JsonValueKind.Object &&
                    dgs.TryGetProperty("model", out var mdl) && mdl.ValueKind == JsonValueKind.Object &&
                    mdl.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                    _modelPath = Path.GetFileName(p.GetString() ?? "");
            }
        }

        // --- /metrics path
        var (status, body) = await http.GetStringAsync("metrics", ct).ConfigureAwait(false);
        if (status == 200 && body.Contains("llamacpp:", StringComparison.Ordinal))
        {
            _metricsEnabled = true;
            return CollectFromMetrics(body, info, now);
        }

        // --- /slots fallback
        _metricsEnabled = false;
        var slots = await http.GetJsonAsync("slots", ct).ConfigureAwait(false);
        if (!slots.HasValue || slots.Value.ValueKind != JsonValueKind.Array)
            return MetricSnapshot.Offline(Kind);

        _lastTicks = now;
        return CollectFromSlots(slots.Value, info, now);
    }

    private MetricSnapshot CollectFromMetrics(
        string body, Dictionary<string, string> info, long now)
    {
        List<PromSample> samples;
        try { samples = PrometheusParser.Parse(body); }
        catch { return MetricSnapshot.Offline(Kind); }
        _lastTicks = now;

        double? Sum(string[] names)
        {
            double sum = 0; bool any = false;
            foreach (var s in samples)
                foreach (var n in names)
                    if (s.Name == n) { sum += s.Value; any = true; break; }
            return any ? sum : null;
        }

        double? First(string[] names)
        {
            foreach (var n in names)
                foreach (var s in samples)
                    if (s.Name == n) return s.Value;
            return null;
        }

        var processing = First(ProcessingNames);
        var deferred = First(DeferredNames);
        var prefillC = Sum(PrefillCounterNames);
        var genC = Sum(GenCounterNames);

        var running = processing.HasValue
            ? MetricValue<int>.Exact((int)Math.Round(processing.Value), MetricSource.NativeMetrics, "llamacpp:requests_processing")
            : MetricValue<int>.None;
        var queued = deferred.HasValue
            ? MetricValue<int>.Exact((int)Math.Round(deferred.Value), MetricSource.NativeMetrics, "llamacpp:requests_deferred")
            : MetricValue<int>.None;

        var prefillRate = prefillC.HasValue ? _prefill.Update(prefillC.Value, now) : MetricValue<double>.None;
        var genRate = genC.HasValue ? _gen.Update(genC.Value, now) : MetricValue<double>.None;

        // llama.cpp exposes no KV usage metric family today — stay honest.
        var kv = MetricValue<double>.None;

        var state = running.HasValue && prefillRate.HasValue && genRate.HasValue && queued.HasValue
            ? ConnectionState.Online : ConnectionState.Limited;

        info["Mode"] = "/metrics (Prometheus)";
        if (_totalSlots.HasValue) info["Slots"] = $"{_totalSlots}";
        if (_modelPath is { } mp) info["Model file"] = mp;

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
            RecentTtftMs = MetricValue<double>.None,
            GeneratedTokensTotal = genC.HasValue
                ? MetricValue<long>.Approx((long)genC.Value, MetricSource.NativeMetrics, "llamacpp:tokens_predicted_total")
                : MetricValue<long>.None,
            PrefilledTokensTotal = prefillC.HasValue
                ? MetricValue<long>.Approx((long)prefillC.Value, MetricSource.NativeMetrics, "llamacpp:prompt_tokens_total")
                : MetricValue<long>.None,
            Requests = null,
            ModelName = _modelPath,
            Info = info,
        };
    }

    private MetricSnapshot CollectFromSlots(JsonElement arr, Dictionary<string, string> info, long now)
    {
        int processing = 0;
        var requests = new List<RequestSnapshot>();
        double decodedTotal = 0;
        double prefillProcessedTotal = 0;

        foreach (var slot in arr.EnumerateArray())
        {
            bool isProcessing =
                (slot.TryGetProperty("is_processing", out var ip) && ip.ValueKind == JsonValueKind.True) ||
                (slot.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String &&
                 st.GetString() is { } s && s.Equals("processing", StringComparison.OrdinalIgnoreCase));
            if (isProcessing) processing++;

            int id = slot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : -1;
            long task = slot.TryGetProperty("id_task", out var tk) && tk.ValueKind == JsonValueKind.Number ? tk.GetInt64() : -1;

            long nDecoded = ReadNDecoded(slot);
            long nPrompt = slot.TryGetProperty("n_prompt_tokens", out var np) && np.ValueKind == JsonValueKind.Number ? np.GetInt64() : -1;
            decodedTotal += Math.Max(0, nDecoded);

            // Accumulate positive n_decoded deltas toward the running total.
            if (nDecoded >= 0 && _slotLastDecoded.TryGetValue(id, out var prev))
            {
                if (nDecoded > prev) _generatedTotal += nDecoded - prev;
            }
            _slotLastDecoded[id] = nDecoded;

            // Prefill progress is tracked only while the slot is actually
            // processing; once generation begins the processed count is static.
            if (isProcessing)
            {
                prefillProcessedTotal += ReadNProcessed(slot);

                long processed = ReadNProcessed(slot);
                if (processed >= 0 && _slotLastProcessed.TryGetValue(id, out var prevProc))
                {
                    if (processed > prevProc) _prefilledTotal += processed - prevProc;
                }
                if (processed >= 0) _slotLastProcessed[id] = processed;
            }

            var req = _slots.Observe(id, task, nPrompt, nDecoded, now, isProcessing);
            if (req != null && isProcessing) requests.Add(req);
        }

        // Aggregate generation rate from total decoded tokens across slots.
        var genRate = _gen.Update(decodedTotal, now);
        // Aggregate prefill rate from total prompt tokens processed across slots.
        var prefillRate = _prefill.Update(prefillProcessedTotal, now);
        var running = processing > 0 || arr.GetArrayLength() >= 0
            ? MetricValue<int>.Exact(processing, MetricSource.NativeApi, "/slots processing count")
            : MetricValue<int>.Exact(0, MetricSource.NativeApi, "/slots");

        var state = ConnectionState.Limited; // metrics disabled => limited by definition

        info["Mode"] = "/slots fallback (enable --metrics for full telemetry)";
        if (_totalSlots.HasValue) info["Slots"] = $"{_totalSlots}";
        if (_modelPath is { } mp) info["Model file"] = mp;

        return new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = state,
            Kind = Kind,
            Running = running,
            Queued = MetricValue<int>.None,
            PrefillTokPerSec = prefillRate,
            GenerationTokPerSec = genRate,
            KvCacheUsage = MetricValue<double>.None,
            RecentTtftMs = MetricValue<double>.None,
            GeneratedTokensTotal = _generatedTotal > 0
                ? MetricValue<long>.Approx(_generatedTotal, MetricSource.Derived, "since monitoring began")
                : MetricValue<long>.None,
            PrefilledTokensTotal = _prefilledTotal > 0
                ? MetricValue<long>.Approx(_prefilledTotal, MetricSource.Derived, "since monitoring began")
                : MetricValue<long>.None,
            Requests = requests.Count > 0 || HasAnyProcessing(arr) ? requests : Array.Empty<RequestSnapshot>(),
            ModelName = _modelPath,
            Info = info,
        };

        static bool HasAnyProcessing(JsonElement a)
        {
            foreach (var s in a.EnumerateArray())
                if ((s.TryGetProperty("is_processing", out var ip) && ip.ValueKind == JsonValueKind.True))
                    return true;
            return false;
        }
    }

    internal static long ReadNProcessed(JsonElement slot)
    {
        if (slot.TryGetProperty("n_prompt_tokens_processed", out var np) && np.ValueKind == JsonValueKind.Number)
            return np.GetInt64();
        return -1;
    }

    internal static long ReadNDecoded(JsonElement slot)
    {
        if (slot.TryGetProperty("n_decoded", out var nd) && nd.ValueKind == JsonValueKind.Number)
            return nd.GetInt64();
        if (slot.TryGetProperty("next_token", out var nt) && nt.ValueKind == JsonValueKind.Array && nt.GetArrayLength() > 0)
        {
            var first = nt[0];
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("n_decoded", out var inner) && inner.ValueKind == JsonValueKind.Number)
                return inner.GetInt64();
        }
        return -1;
    }

    public TelemetryHelp GetHelp()
    {
        var metricsOn = _metricsEnabled == true;
        return new TelemetryHelp(
            metricsOn ? "Full telemetry via /metrics" : "Limited telemetry",
            metricsOn
                ? ["Running requests", "Queue", "Prefill throughput", "Generation throughput"]
                : ["Running requests", "Per-slot generation rate"],
            metricsOn
                ? []
                : ["Queue depth", "Prefill throughput", "KV-cache usage"],
            "llama-server ... --metrics",
            _currentCommand);
    }

    private string? _currentCommand;

    /// <summary>Set by discovery when the local process command line was recovered.</summary>
    public void SetCurrentCommand(string cmd) => _currentCommand = cmd;
}
