namespace LLMMeter.Core;

[Flags]
public enum BackendCapabilities
{
    None = 0,
    RunningRequests = 1 << 0,
    QueuedRequests = 1 << 1,
    AggregatePrefillRate = 1 << 2,
    AggregateGenerationRate = 1 << 3,
    KvCacheUsage = 1 << 4,
    ActiveRequestEnumeration = 1 << 5,
    PerRequestInputTokens = 1 << 6,
    PerRequestOutputTokens = 1 << 7,
    PerRequestGenerationRate = 1 << 8,
    RecentRequestTtft = 1 << 9,
}

/// <summary>Where an endpoint physically lives. Never flattened into a generic localhost.</summary>
public enum OriginKind
{
    WindowsHost,
    Wsl,
    Manual,
}

/// <summary>An HTTP inference server endpoint.</summary>
public sealed record EndpointRef(
    string Id,              // stable identity: origin|host|port
    Uri BaseUrl,
    OriginKind Origin,
    string? WslDistro,      // non-null when Origin == Wsl
    string? AuthToken = null)
{
    public string HostPort => BaseUrl.IsDefaultPort ? BaseUrl.Authority : $"{BaseUrl.Host}:{BaseUrl.Port}";

    public string DedupeKey => NormalizeEndpointKey(BaseUrl);

    public static string NormalizeEndpointKey(Uri url)
    {
        string host = url.Host.ToLowerInvariant();
        if (host is "localhost" or "::1") host = "127.0.0.1";
        int port = url.IsDefaultPort ? (url.Scheme == "https" ? 443 : 80) : url.Port;
        string path = url.AbsolutePath.TrimEnd('/');
        return $"{url.Scheme.ToLowerInvariant()}://{host}:{port}{path}";
    }
}

public enum BackendKind
{
    Unknown,
    Vllm,
    LlamaCpp,
    LmStudio,
    Ollama,
    GenericOpenAi,
    NInfer,
}

public static class BackendKindExtensions
{
    public static string DisplayName(this BackendKind kind) => kind switch
    {
        BackendKind.Vllm => "vLLM",
        BackendKind.LlamaCpp => "llama-server",
        BackendKind.LmStudio => "LM Studio",
        BackendKind.Ollama => "Ollama",
        BackendKind.GenericOpenAi => "OpenAI-compatible",
        BackendKind.NInfer => "NInfer",
        _ => "Unknown",
    };
}

/// <summary>
/// A selectable monitoring target. An endpoint may expose multiple targets
/// (e.g. one per loaded model). Targets referencing the same endpoint share one collector.
/// </summary>
public sealed record BackendTarget(
    string Id,
    EndpointRef Endpoint,
    BackendKind Kind,
    string? ModelId,        // model-scoped target when supported (llama-server router)
    string DisplayName)
{
    public string GroupKey => $"{Endpoint.Id}|{(RequiresModelScopedCollector ? ModelId : "*")}";

    /// <summary>
    /// True only for backends that genuinely require a model-scoped collector instance (currently llama-server router).
    /// LM Studio and other multi-model backends use endpoint-level collectors.
    /// </summary>
    public bool RequiresModelScopedCollector => Kind == BackendKind.LlamaCpp && !string.IsNullOrEmpty(ModelId);
}
