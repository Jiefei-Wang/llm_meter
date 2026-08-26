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
    private readonly string? _modelId;
    internal Func<long> Clock = () => MonoClock.NowTicks;

    // /metrics state
    private bool? _metricsEnabled;
    private bool _slotsAvailable;
    private long _lastTicks;

    // /props state
    private int? _totalSlots;
    private string? _modelPath;
    private long _lastPropsAttemptTicks;

    // Cumulative generated and prefilled tokens (since monitoring began).
    // Uses task-aware state so task changes and slot reuse re-baseline instead of cross-contaminating.
    private sealed class SlotCounterState
    {
        public long TaskId = -1;
        public long LastDecoded = -1;
        public long LastProcessed = -1;
    }

    private readonly Dictionary<int, SlotCounterState> _slotStates = new();
    private long _generatedTotal;
    private long _prefilledTotal;

    internal static readonly string[] ProcessingNames = ["llamacpp:requests_processing"];
    internal static readonly string[] DeferredNames = ["llamacpp:requests_deferred"];
    internal static readonly string[] PrefillCounterNames = ["llamacpp:prompt_tokens_total"];
    internal static readonly string[] GenCounterNames = ["llamacpp:tokens_predicted_total"];
    internal static readonly string[] PrefillGaugeNames = ["llamacpp:prompt_tokens_seconds"];
    internal static readonly string[] GenGaugeNames = ["llamacpp:predicted_tokens_seconds"];

    public LlamaCppAdapter(string? modelId = null)
    {
        _modelId = modelId;
        if (!string.IsNullOrEmpty(modelId))
            _modelPath = modelId;
    }

    public string? ModelId => _modelId;

    private string EndpointPath(string baseEndpoint)
    {
        if (string.IsNullOrEmpty(_modelId))
            return baseEndpoint;
        string separator = baseEndpoint.Contains('?') ? "&" : "?";
        return $"{baseEndpoint}{separator}model={Uri.EscapeDataString(_modelId)}&autoload=false";
    }

    public BackendCapabilities Capabilities =>
        BackendCapabilities.RunningRequests                       // both modes
        | BackendCapabilities.AggregateGenerationRate             // counters or slot deltas
        | (_metricsEnabled == true
            ? BackendCapabilities.QueuedRequests
              | BackendCapabilities.AggregatePrefillRate
              | (_slotsAvailable
                  ? BackendCapabilities.ActiveRequestEnumeration
                    | BackendCapabilities.PerRequestInputTokens
                    | BackendCapabilities.PerRequestOutputTokens
                    | BackendCapabilities.PerRequestGenerationRate
                  : 0)
            : 0)
        | (_metricsEnabled == false
            ? BackendCapabilities.ActiveRequestEnumeration
              | BackendCapabilities.AggregatePrefillRate
              | BackendCapabilities.PerRequestInputTokens
              | BackendCapabilities.PerRequestOutputTokens
              | BackendCapabilities.PerRequestGenerationRate
            : 0);

    public async Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct)
    {
        var (status, body) = await http.GetStringAsync(EndpointPath("metrics"), ct).ConfigureAwait(false);
        if (status == 200 && body.Contains("llamacpp:", StringComparison.Ordinal))
            return new FingerprintResult(Kind, "/metrics contains llamacpp:* families");

        var slots = await http.GetJsonAsync(EndpointPath("slots"), ct).ConfigureAwait(false);
        if (slots.HasValue && LooksLikeSlots(slots.Value))
            return new FingerprintResult(Kind, "/slots returns llama.cpp slot structures");

        // Check for llama-server router / multi-model mode
        if (string.IsNullOrEmpty(_modelId))
        {
            var routerResult = await IdentifyRouterAsync(http, ct).ConfigureAwait(false);
            if (routerResult != null)
                return routerResult;
        }

        return null;
    }

    private async Task<FingerprintResult?> IdentifyRouterAsync(IHttp http, CancellationToken ct)
    {
        // 1. Upstream /props returns role: "router" (case-insensitive) in router mode.
        // This detects router mode even when 0 models are loaded/configured.
        var props = await http.GetJsonAsync("props", ct).ConfigureAwait(false);
        if (props.HasValue && props.Value.ValueKind == JsonValueKind.Object)
        {
            if (props.Value.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String &&
                roleEl.GetString()?.Equals("router", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new FingerprintResult(Kind, "llama-server router mode (/props role=router)");
            }
        }

        // 2. Query catalog of models
        var allModels = await EnumerateModelsDetailedAsync(http, ct).ConfigureAwait(false);
        var loadedModels = allModels.Where(m => m.Status.Equals("loaded", StringComparison.OrdinalIgnoreCase)).ToList();

        // Only probe a model if it is already loaded; never probe unloaded models to avoid autoloading.
        if (loadedModels.Count > 0)
        {
            string firstModel = loadedModels[0].Id;
            string probePath = $"metrics?model={Uri.EscapeDataString(firstModel)}&autoload=false";
            var (status, body) = await http.GetStringAsync(probePath, ct).ConfigureAwait(false);
            if (status == 200 && body.Contains("llamacpp:", StringComparison.Ordinal))
                return new FingerprintResult(Kind, $"llama-server router mode ({allModels.Count} models)");

            var slots = await http.GetJsonAsync($"slots?model={Uri.EscapeDataString(firstModel)}&autoload=false", ct).ConfigureAwait(false);
            if (slots.HasValue && LooksLikeSlots(slots.Value))
                return new FingerprintResult(Kind, $"llama-server router mode via /slots ({allModels.Count} models)");

            var modelProps = await http.GetJsonAsync($"props?model={Uri.EscapeDataString(firstModel)}&autoload=false", ct).ConfigureAwait(false);
            if (modelProps.HasValue && (modelProps.Value.TryGetProperty("total_slots", out _) || modelProps.Value.TryGetProperty("model_path", out _)))
                return new FingerprintResult(Kind, $"llama-server router mode via /props ({allModels.Count} models)");
        }

        return null;
    }


    private List<LlamaRouterModel> _cachedRouterModels = [];
    private long _lastModelsProbeTicks;

    internal static async Task<List<LlamaRouterModel>> EnumerateModelsDetailedAsync(IHttp http, CancellationToken ct)
    {
        var list = new List<LlamaRouterModel>();
        var models = await http.GetJsonAsync("models", ct).ConfigureAwait(false);
        if (models.HasValue)
        {
            if (models.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) &&
                        id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        string status = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                            ? st.GetString() ?? "loaded" : "loaded";
                        list.Add(new LlamaRouterModel(id.GetString()!, status));
                    }
                    else if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        list.Add(new LlamaRouterModel(item.GetString()!, "loaded"));
                    }
                }
            }
            else if (models.Value.ValueKind == JsonValueKind.Object &&
                     models.Value.TryGetProperty("models", out var inner) && inner.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in inner.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        string? name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        name ??= item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            string status = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                                ? st.GetString() ?? "loaded" : "loaded";
                            list.Add(new LlamaRouterModel(name, status));
                        }
                    }
                }
            }
        }

        if (list.Count > 0) return list;

        var v1 = await http.GetJsonAsync("v1/models", ct).ConfigureAwait(false);
        if (v1.HasValue && v1.Value.ValueKind == JsonValueKind.Object &&
            v1.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    string status = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString() ?? "loaded" : "loaded";
                    list.Add(new LlamaRouterModel(id.GetString()!, status));
                }
            }
        }

        return list;
    }

    internal static async Task<List<string>> EnumerateModelsAsync(IHttp http, CancellationToken ct)
    {
        var detailed = await EnumerateModelsDetailedAsync(http, ct).ConfigureAwait(false);
        return detailed
            .Where(m => m.Status.Equals("loaded", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .ToList();
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
        var now = Clock();
        var info = new Dictionary<string, string>();

        // --- /props occasionally for metadata
        bool isRouter = false;
        if ((_modelPath is null || _totalSlots is null) &&
            (_lastPropsAttemptTicks == 0 || now - _lastPropsAttemptTicks >= Stopwatch.Frequency * 5))
        {
            _lastPropsAttemptTicks = now;
            var props = await http.GetJsonAsync(EndpointPath("props"), ct).ConfigureAwait(false);
            if (props.HasValue && props.Value.ValueKind == JsonValueKind.Object)
            {
                if (props.Value.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String &&
                    roleEl.GetString()?.Equals("router", StringComparison.OrdinalIgnoreCase) == true)
                {
                    isRouter = true;
                }
                if (props.Value.TryGetProperty("total_slots", out var ts) && ts.ValueKind == JsonValueKind.Number)
                    _totalSlots = ts.GetInt32();
                if (props.Value.TryGetProperty("model_path", out var mp) && mp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(mp.GetString()))
                    _modelPath = Path.GetFileName(mp.GetString());
                else if (props.Value.TryGetProperty("default_generation_settings", out var dgs) &&
                    dgs.ValueKind == JsonValueKind.Object &&
                    dgs.TryGetProperty("model", out var mdl) && mdl.ValueKind == JsonValueKind.Object &&
                    mdl.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(p.GetString()))
                    _modelPath = Path.GetFileName(p.GetString());
            }
        }

        // Fetch these together. /metrics can be a relatively expensive scrape on
        // a busy server, and waiting for it before /slots made the UI feel stale.
        // /slots is also needed for the active-request rows: Prometheus only has
        // aggregate request gauges.
        var metricsTask = http.GetStringAsync(EndpointPath("metrics"), ct);
        var slotsTask = http.GetJsonAsync(EndpointPath("slots"), ct);
        await Task.WhenAll(metricsTask, slotsTask).ConfigureAwait(false);
        var (status, body) = await metricsTask.ConfigureAwait(false);
        var slots = await slotsTask.ConfigureAwait(false);

        // --- /metrics path
        if (status == 200 && body.Contains("llamacpp:", StringComparison.Ordinal))
        {
            _metricsEnabled = true;
            _slotsAvailable = slots.HasValue && slots.Value.ValueKind == JsonValueKind.Array;
            var snapshot = CollectFromMetrics(body, info, now);
            return _slotsAvailable
                ? WithRequests(snapshot, CollectRequestRows(slots!.Value, now))
                : snapshot;
        }

        // Check for router mode when not already scoped to a specific model
        if (string.IsNullOrEmpty(_modelId))
        {
            if (_lastModelsProbeTicks == 0 || now - _lastModelsProbeTicks >= Stopwatch.Frequency * 5)
            {
                _lastModelsProbeTicks = now;
                _cachedRouterModels = await EnumerateModelsDetailedAsync(http, ct).ConfigureAwait(false);
            }

            var loadedModels = _cachedRouterModels
                .Where(m => m.Status.Equals("loaded", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Id)
                .ToList();

            if (isRouter || _cachedRouterModels.Count > 0)
            {
                info["Router"] = "true";
                info["Mode"] = $"router mode ({loadedModels.Count} loaded, {_cachedRouterModels.Count} total)";
                return new MetricSnapshot
                {
                    Timestamp = DateTimeOffset.Now,
                    State = loadedModels.Count > 0 ? ConnectionState.Online : ConnectionState.Limited,
                    Kind = Kind,
                    Running = MetricValue<int>.None,
                    Queued = MetricValue<int>.None,
                    PrefillTokPerSec = MetricValue<double>.None,
                    GenerationTokPerSec = MetricValue<double>.None,
                    KvCacheUsage = MetricValue<double>.None,
                    RecentTtftMs = MetricValue<double>.None,
                    Requests = null,
                    ModelName = _modelPath ?? (loadedModels.Count > 0 ? loadedModels[0] : (_cachedRouterModels.Count > 0 ? _cachedRouterModels[0].Id : "router")),
                    LoadedModels = loadedModels,
                    Info = info,
                };
            }
        }


        // --- /slots fallback
        _metricsEnabled = false;
        _slotsAvailable = slots.HasValue && slots.Value.ValueKind == JsonValueKind.Array;
        if (!slots.HasValue || slots.Value.ValueKind != JsonValueKind.Array)
            return MetricSnapshot.Offline(Kind);

        _lastTicks = now;
        return CollectFromSlots(slots.Value, info, now);
    }

    private IReadOnlyList<RequestSnapshot> CollectRequestRows(JsonElement arr, long now)
    {
        var requests = new List<RequestSnapshot>();
        foreach (var slot in arr.EnumerateArray())
        {
            bool processing =
                (slot.TryGetProperty("is_processing", out var ip) && ip.ValueKind == JsonValueKind.True) ||
                (slot.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String &&
                 st.GetString() is { } status && status.Equals("processing", StringComparison.OrdinalIgnoreCase));
            if (!processing) continue;

            int id = slot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : -1;
            long task = slot.TryGetProperty("id_task", out var tk) && tk.ValueKind == JsonValueKind.Number ? tk.GetInt64() : -1;
            long prompt = ReadNPrompt(slot);
            long evaluated = ReadNProcessed(slot);
            long cached = ReadNCached(slot);
            long input = evaluated >= 0 && cached >= 0 ? evaluated + cached : prompt;
            var request = _slots.Observe(id, task, input, ReadNDecoded(slot), now, true, evaluated, cached);
            if (request is not null) requests.Add(request);
        }
        return requests;
    }

    internal static MetricSnapshot WithRequests(MetricSnapshot snapshot, IReadOnlyList<RequestSnapshot> requests)
    {
        var livePrefill = ResolveAggregateRate(
            requests,
            isCandidate: r => (!r.OutputTokens.HasValue || r.OutputTokens.Value == 0) || r.PrefillTokensPerSecond.HasValue,
            rateSelector: r => r.PrefillTokensPerSecond,
            fallbackAggregate: snapshot.PrefillTokPerSec,
            rateDescription: "prefill");

        var liveDecode = ResolveAggregateRate(
            requests,
            isCandidate: r => (r.OutputTokens.HasValue && r.OutputTokens.Value > 0) || r.TokensPerSecond.HasValue,
            rateSelector: r => r.TokensPerSecond,
            fallbackAggregate: snapshot.GenerationTokPerSec,
            rateDescription: "generation");

        return new MetricSnapshot
        {
            Timestamp = snapshot.Timestamp,
            State = snapshot.State,
            Kind = snapshot.Kind,
            Running = snapshot.Running,
            Queued = snapshot.Queued,
            PrefillTokPerSec = livePrefill.HasValue ? livePrefill : snapshot.PrefillTokPerSec,
            GenerationTokPerSec = liveDecode.HasValue ? liveDecode : snapshot.GenerationTokPerSec,
            KvCacheUsage = snapshot.KvCacheUsage,
            RecentTtftMs = snapshot.RecentTtftMs,
            GeneratedTokensTotal = snapshot.GeneratedTokensTotal,
            PrefilledTokensTotal = snapshot.PrefilledTokensTotal,
            Requests = requests,
            ModelName = snapshot.ModelName,
            LoadedModels = snapshot.LoadedModels,
            Info = snapshot.Info,
        };
    }

    private static MetricValue<double> ResolveAggregateRate(
        IReadOnlyList<RequestSnapshot> requests,
        Func<RequestSnapshot, bool> isCandidate,
        Func<RequestSnapshot, MetricValue<double>> rateSelector,
        MetricValue<double> fallbackAggregate,
        string rateDescription)
    {
        var candidates = requests.Where(isCandidate).ToList();
        if (candidates.Count == 0)
            return fallbackAggregate;

        var withValidRate = candidates.Where(r => rateSelector(r).HasValue).ToList();
        if (withValidRate.Count == candidates.Count)
        {
            return MetricValue<double>.Approx(
                withValidRate.Sum(r => rateSelector(r).Value),
                MetricSource.Derived,
                $"sum of live /slots {rateDescription} rates");
        }

        if (fallbackAggregate.HasValue)
            return fallbackAggregate;

        if (withValidRate.Count > 0)
        {
            return MetricValue<double>.Approx(
                withValidRate.Sum(r => rateSelector(r).Value),
                MetricSource.Derived,
                $"partial sum of live /slots {rateDescription} rates");
        }

        return MetricValue<double>.None;
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
        if (!prefillRate.HasValue)
        {
            var pGauge = First(PrefillGaugeNames);
            if (pGauge.HasValue)
                prefillRate = MetricValue<double>.Exact(pGauge.Value, MetricSource.NativeMetrics, "llamacpp:prompt_tokens_seconds");
        }

        var genRate = genC.HasValue ? _gen.Update(genC.Value, now) : MetricValue<double>.None;
        if (!genRate.HasValue)
        {
            var gGauge = First(GenGaugeNames);
            if (gGauge.HasValue)
                genRate = MetricValue<double>.Exact(gGauge.Value, MetricSource.NativeMetrics, "llamacpp:predicted_tokens_seconds");
        }

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
            long nPrompt = ReadNPrompt(slot);
            long processed = isProcessing ? ReadNProcessed(slot) : -1;
            long cached = ReadNCached(slot);
            long input = processed >= 0 && cached >= 0 ? processed + cached : nPrompt;

            if (id >= 0)
            {
                UpdateSlotTotals(id, task, nDecoded, processed);
            }

            var req = _slots.Observe(id, task, input, nDecoded, now, isProcessing, processed, cached);
            if (req != null && isProcessing) requests.Add(req);
        }

        // Aggregate generation rate from valid per-processing-slot generation rates
        var validGenRates = requests.Where(r => r.TokensPerSecond.HasValue).Select(r => r.TokensPerSecond.Value).ToList();
        var genRate = validGenRates.Count > 0
            ? MetricValue<double>.Approx(validGenRates.Sum(), MetricSource.Derived, "sum of live /slots rates")
            : MetricValue<double>.None;

        // Aggregate prefill rate from valid per-processing-slot prefill rates
        var validPrefillRates = requests.Where(r => r.PrefillTokensPerSecond.HasValue).Select(r => r.PrefillTokensPerSecond.Value).ToList();
        var prefillRate = validPrefillRates.Count > 0
            ? MetricValue<double>.Approx(validPrefillRates.Sum(), MetricSource.Derived, "sum of live /slots rates")
            : MetricValue<double>.None;

        var running = MetricValue<int>.Exact(processing, MetricSource.NativeApi, "/slots processing count");

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

    private void UpdateSlotTotals(int slotId, long taskId, long nDecoded, long processed)
    {
        if (!_slotStates.TryGetValue(slotId, out var state))
        {
            state = new SlotCounterState
            {
                TaskId = taskId,
                LastDecoded = nDecoded,
                LastProcessed = processed,
            };
            _slotStates[slotId] = state;
            return;
        }

        if (state.TaskId != taskId)
        {
            // Task changed: re-baseline without computing delta against previous task
            state.TaskId = taskId;
            state.LastDecoded = nDecoded;
            state.LastProcessed = processed;
            return;
        }

        // Same task: accumulate positive deltas
        if (nDecoded >= 0)
        {
            if (state.LastDecoded >= 0 && nDecoded > state.LastDecoded)
            {
                _generatedTotal += (nDecoded - state.LastDecoded);
            }
            state.LastDecoded = nDecoded;
        }

        if (processed >= 0)
        {
            if (state.LastProcessed >= 0 && processed > state.LastProcessed)
            {
                _prefilledTotal += (processed - state.LastProcessed);
            }
            state.LastProcessed = processed;
        }
    }

    internal static long ReadNProcessed(JsonElement slot)
    {
        if (slot.TryGetProperty("n_prompt_tokens_processed", out var np) && np.ValueKind == JsonValueKind.Number)
            return np.GetInt64();
        return -1;
    }

    internal static long ReadNCached(JsonElement slot)
    {
        if (slot.TryGetProperty("n_prompt_tokens_cache", out var nc) && nc.ValueKind == JsonValueKind.Number)
            return nc.GetInt64();
        return -1;
    }

    private static long ReadNPrompt(JsonElement slot)
    {
        if (slot.TryGetProperty("n_prompt_tokens", out var np) && np.ValueKind == JsonValueKind.Number)
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
                : ["Running requests", "Per-slot prefill and generation rates"],
            metricsOn
                ? []
                : ["Queue depth", "KV-cache usage"],
            "llama-server ... --metrics",
            _currentCommand);
    }

    private string? _currentCommand;

    /// <summary>Set by discovery when the local process command line was recovered.</summary>
    public void SetCurrentCommand(string cmd) => _currentCommand = cmd;
}

public sealed record LlamaRouterModel(string Id, string Status);
