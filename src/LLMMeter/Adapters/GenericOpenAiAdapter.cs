using System.Text.Json;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// Generic OpenAI-compatible endpoint: health + model listing only.
/// Never claims to be vLLM; telemetry stays unavailable until a known
/// metrics adapter positively identifies the server.
/// </summary>
public sealed class GenericOpenAiAdapter : IBackendAdapter
{
    public BackendKind Kind => BackendKind.GenericOpenAi;

    public BackendCapabilities Capabilities => BackendCapabilities.None;

    public async Task<FingerprintResult?> IdentifyAsync(IHttp http, CancellationToken ct)
    {
        var models = await http.GetJsonAsync("v1/models", ct).ConfigureAwait(false);
        if (models.HasValue && LmStudioAdapter.LooksLikeGenericList(models.Value))
            return new FingerprintResult(Kind, "/v1/models responds with OpenAI list schema");
        return null;
    }

    public async Task<MetricSnapshot> CollectAsync(IHttp http, CancellationToken ct)
    {
        var models = await http.GetJsonAsync("v1/models", ct).ConfigureAwait(false);
        if (!models.HasValue || !LmStudioAdapter.LooksLikeGenericList(models.Value))
            return MetricSnapshot.Offline(Kind);

        var loaded = new List<string>();
        string? first = null;
        foreach (var m in models.Value.GetProperty("data").EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                loaded.Add(id.GetString()!);
                first ??= loaded[^1];
            }
        }

        return new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Limited,
            Kind = Kind,
            Requests = null,
            ModelName = first,
            LoadedModels = loaded,
            Info = new Dictionary<string, string> { ["API"] = "/v1/models" },
        };
    }

    public TelemetryHelp GetHelp() => new(
        "Limited telemetry",
        ["Health status", "Model listing"],
        ["Running requests", "Queue", "Prefill throughput", "Generation throughput"],
        null,
        "This server identifies as generic OpenAI-compatible. No recognized metrics " +
        "endpoint was found, so runtime telemetry is unavailable.");
}
