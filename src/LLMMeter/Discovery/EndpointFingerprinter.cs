using LLMMeter.Adapters;
using LLMMeter.Core;

namespace LLMMeter.Discovery;

/// <summary>
/// Fingerprints an HTTP endpoint by behavior — never by port number.
/// Order matters: specific engines first, generic OpenAI last
/// (llama.cpp and Ollama also expose /v1/models).
/// </summary>
public sealed class EndpointFingerprinter
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(600);

    private readonly List<IBackendAdapter> _ordered;
    private readonly Func<Uri, IHttp>? _httpFactory;

    public EndpointFingerprinter(Func<Uri, IHttp>? httpFactory = null)
    {
        _httpFactory = httpFactory;
        _ordered =
        [
            new VllmAdapter(),
            new LlamaCppAdapter(),
            new LmStudioAdapter(),
            new OllamaAdapter(),
        ];
    }

    /// <summary>Identify the endpoint kind. Returns GenericOpenAi or Unknown.</summary>
    public async Task<FingerprintResult> FingerprintAsync(Uri baseUrl, CancellationToken ct)
    {
        using var inner = _httpFactory?.Invoke(baseUrl) ?? HttpService.CreateOwning(baseUrl, ProbeTimeout);
        using var http = new CachingHttp(inner);
        foreach (var adapter in _ordered)
        {
            try
            {
                var res = await adapter.IdentifyAsync(http, ct).ConfigureAwait(false);
                if (res != null) return res;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // probe failures move on to the next candidate
            }
        }

        var generic = new GenericOpenAiAdapter();
        try
        {
            var g = await generic.IdentifyAsync(http, ct).ConfigureAwait(false);
            if (g != null) return g;
        }
        catch { /* fall through */ }

        return new FingerprintResult(BackendKind.Unknown, "no recognized endpoint schema");
    }

    /// <summary>One fingerprint pass asks several adapters about the same paths.</summary>
    private sealed class CachingHttp(IHttp inner) : IHttp
    {
        private readonly Dictionary<string, Task<(int Status, string Body)>> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public Uri BaseUrl => inner.BaseUrl;
        public TimeSpan Timeout => inner.Timeout;

        public Task<(int Status, string Body)> GetStringAsync(string path, CancellationToken ct)
        {
            string key = path.TrimStart('/').TrimEnd('/');
            if (_responses.TryGetValue(key, out var cached)) return cached;
            var request = inner.GetStringAsync(key, ct);
            _responses[key] = request;
            return request;
        }

        public void Dispose() { }
    }
}
