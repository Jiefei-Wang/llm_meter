using System.Text.Json;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// LM Studio adapter. Identifies the server and lists loaded models via the
/// official REST API (/api/v0/models, fallback /api/v1/models). Passive
/// server-wide runtime telemetry is not exposed by current APIs — those
/// metrics stay honestly unavailable.
/// </summary>
public sealed class LmStudioAdapter : IBackendAdapter
{
    public BackendKind Kind => BackendKind.LmStudio;

    public BackendCapabilities Capabilities => BackendCapabilities.None;

    private string? _serverVersion;

    public async Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct)
    {
        var v0 = await http.GetJsonAsync("api/v0/models", ct).ConfigureAwait(false);
        if (v0.HasValue && LooksLikeLmStudio(v0.Value))
            return new FingerprintResult(Kind, "/api/v0/models matches LM Studio schema");

        var v1 = await http.GetJsonAsync("api/v1/models", ct).ConfigureAwait(false);
        if (v1.HasValue && LooksLikeNativeV1(v1.Value))
            return new FingerprintResult(Kind, "/api/v1/models responds with list schema");
        return null;
    }

    internal static bool LooksLikeLmStudio(JsonElement el)
    {
        if (!LooksLikeGenericList(el)) return false;
        foreach (var m in el.GetProperty("data").EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            // v0 adds LM-Studio-specific fields
            if (m.TryGetProperty("state", out _) ||
                m.TryGetProperty("max_context_length", out _) ||
                m.TryGetProperty("compatibility_type", out _))
                return true;
        }
        // The path itself is LM Studio-specific; an empty catalog is valid.
        return el.GetProperty("data").GetArrayLength() == 0;
    }

    internal static bool LooksLikeGenericList(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.String ||
            obj.GetString() != "list") return false;
        if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return false;

        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            return m.TryGetProperty("id", out _) && m.TryGetProperty("object", out _);
        }
        return data.GetArrayLength() == 0; // empty list still proves "list" schema
    }

    internal static bool LooksLikeNativeV1(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object) return false;
            return model.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String &&
                   model.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                   model.TryGetProperty("loaded_instances", out var loaded) && loaded.ValueKind == JsonValueKind.Array;
        }
        return true;
    }

    public async Task<MetricSnapshot> CollectAsync(IHttp http, CancellationToken ct)
    {
        if (_serverVersion is null)
        {
            var status = await http.GetJsonAsync("api/v0/status", ct).ConfigureAwait(false);
            if (status.HasValue && status.Value.ValueKind == JsonValueKind.Object &&
                status.Value.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
                _serverVersion = ver.GetString();
        }

        var info = new Dictionary<string, string>();
        var models = await http.GetJsonAsync("api/v0/models", ct).ConfigureAwait(false);

        bool isV0 = models.HasValue && LooksLikeLmStudio(models.Value);
        bool isV1 = false;
        if (!isV0)
        {
            models = await http.GetJsonAsync("api/v1/models", ct).ConfigureAwait(false);
            isV1 = models.HasValue && LooksLikeNativeV1(models.Value);
            if (!isV1)
                return MetricSnapshot.Offline(Kind);
        }

        var loaded = new List<string>();
        string? firstLoadedId = null;

        if (isV1)
        {
            foreach (var m in models!.Value.GetProperty("models").EnumerateArray())
            {
                if (!m.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                    !m.TryGetProperty("loaded_instances", out var instances) || instances.ValueKind != JsonValueKind.Array ||
                    instances.GetArrayLength() == 0) continue;
                string id = key.GetString() ?? "";
                if (id.Length == 0) continue;
                loaded.Add(id);
                firstLoadedId ??= id;
                var instance = instances[0];
                if (instance.ValueKind == JsonValueKind.Object && instance.TryGetProperty("config", out var config) &&
                    config.ValueKind == JsonValueKind.Object && config.TryGetProperty("context_length", out var ctx) &&
                    ctx.ValueKind == JsonValueKind.Number)
                    info[$"{id} ctx"] = $"{ctx.GetInt64():0}";
            }
        }
        else
        {
            foreach (var m in models!.Value.GetProperty("data").EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                string id = idEl.GetString() ?? "";
                string state = m.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
                if (state == "loaded")
                {
                    loaded.Add(id);
                    firstLoadedId ??= id;
                }
                if (state == "loaded" && m.TryGetProperty("max_context_length", out var mcl) && mcl.ValueKind == JsonValueKind.Number)
                    info[$"{id} ctx"] = $"{mcl.GetInt64():0}";
            }
        }

        info["API"] = isV1 ? "REST API v1" : "REST API v0";
        if (_serverVersion is { } sv) info["Server"] = sv;

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
            ModelName = firstLoadedId,
            LoadedModels = loaded,
            Info = info,
        };
    }

    public TelemetryHelp GetHelp() => new(
        "Limited telemetry",
        ["Server status", "Loaded model discovery", "Model metadata"],
        ["Running requests", "Queue", "Prefill throughput", "Generation throughput", "KV usage", "Per-request details"],
        null,
        $"LM Studio's local REST API does not expose passive server-wide runtime metrics{_serverVersion,0}. " +
        "These values are shown as unavailable rather than estimated.");
}
