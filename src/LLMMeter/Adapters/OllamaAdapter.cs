using System.Text.Json;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// Ollama adapter: /api/version + /api/ps for loaded models. The official
/// server API exposes no passive request/queue/token-rate telemetry, so those
/// remain unavailable (never fabricated).
/// </summary>
public sealed class OllamaAdapter : IBackendAdapter
{
    public BackendKind Kind => BackendKind.Ollama;

    public BackendCapabilities Capabilities => BackendCapabilities.None;

    private string? _version;

    public async Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct)
    {
        var ps = await http.GetJsonAsync("api/ps", ct).ConfigureAwait(false);
        if (ps.HasValue && LooksLikeOllamaPs(ps.Value))
            return new FingerprintResult(Kind, "/api/ps matches Ollama schema");

        var ver = await http.GetJsonAsync("api/version", ct).ConfigureAwait(false);
        if (ver.HasValue && ver.Value.ValueKind == JsonValueKind.Object &&
            ver.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
        {
            // version alone could be anything; confirm with tags schema
            var tags = await http.GetJsonAsync("api/tags", ct).ConfigureAwait(false);
            if (tags.HasValue && tags.Value.ValueKind == JsonValueKind.Object &&
                tags.Value.TryGetProperty("models", out _) )
                return new FingerprintResult(Kind, "/api/version + /api/tags match Ollama schema");
        }
        return null;
    }

    internal static bool LooksLikeOllamaPs(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryGet(el, "models", "Models", out var models) || models.ValueKind != JsonValueKind.Array) return false;

        foreach (var m in models.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            bool hasName = TryGet(m, "name", "Name", out _) || TryGet(m, "model", "Model", out _);
            return hasName;
        }
        return true; // empty Models array is valid Ollama
    }

    public async Task<MetricSnapshot> CollectAsync(IHttp http, CancellationToken ct)
    {
        if (_version is null)
        {
            var ver = await http.GetJsonAsync("api/version", ct).ConfigureAwait(false);
            if (ver.HasValue && ver.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                _version = v.GetString();
        }

        var ps = await http.GetJsonAsync("api/ps", ct).ConfigureAwait(false);
        if (!ps.HasValue || !LooksLikeOllamaPs(ps.Value))
            return MetricSnapshot.Offline(Kind);

        var info = new Dictionary<string, string>();
        var loaded = new List<string>();
        string? first = null;

        TryGet(ps.Value, "models", "Models", out var models);
        foreach (var m in models.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            string name = TryGet(m, "name", "Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
            if (name.Length == 0) continue;
            loaded.Add(name);
            first ??= name;

            if (TryGet(m, "size_vram", "SizeVRAM", out var vr) && vr.ValueKind == JsonValueKind.Number)
                info[$"{name} VRAM"] = FormatBytes(vr.GetDouble());
            if (TryGet(m, "expires_at", "ExpiresAt", out var ex) && ex.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ex.GetString(), out var exp))
            {
                var keep = exp - DateTimeOffset.Now;
                if (keep > TimeSpan.Zero)
                    info[$"{name} unload"] = $"in {FormatDuration(keep)}";
            }
        }

        info["API"] = "/api/ps";
        if (_version is { } v2) info["Server"] = $"v{v2}";

        return new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Limited,
            Kind = Kind,
            Running = MetricValue<int>.None,
            Queued = MetricValue<int>.None,
            PrefillTokPerSec = MetricValue<double>.None,
            GenerationTokPerSec = MetricValue<double>.None,
            KvCacheUsage = MetricValue<double>.None,
            RecentTtftMs = MetricValue<double>.None,
            Requests = null,
            ModelName = first,
            LoadedModels = loaded,
            Info = info,
        };
    }

    private static bool TryGet(JsonElement element, string current, string legacy, out JsonElement value) =>
        element.TryGetProperty(current, out value) || element.TryGetProperty(legacy, out value);

    internal static string FormatBytes(double b) =>
        b >= 1 << 30 ? $"{b / (1 << 30):0.0#} GB" :
        b >= 1 << 20 ? $"{b / (1 << 20):0} MB" :
        $"{b / (1 << 10):0} KB";

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0}h {t.Minutes:0}m" :
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0}m" :
        $"{t.Seconds:0}s";

    public TelemetryHelp GetHelp() => new(
        "Limited telemetry",
        ["Server status", "Loaded models", "VRAM/unload metadata"],
        ["Running requests", "Queue", "Prefill throughput", "Generation throughput", "Per-request details"],
        null,
        "Ollama's local API does not currently expose passive server-wide runtime " +
        "telemetry to third parties, so these values display as unavailable.");
}
