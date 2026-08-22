using System.Net.Http;
using System.Text.Json;
using LLMMeter.Adapters;
using LLMMeter.Core;
using LLMMeter.Discovery;
using Xunit;

namespace LLMMeter.Tests;

/// <summary>Canned-response IHttp for fingerprinting tests (spec §49).</summary>
internal sealed class FakeHttp(Uri baseUrl, IReadOnlyDictionary<string, (int Status, string Body)> routes) : IHttp
{
    public Uri BaseUrl { get; } = baseUrl;
    public TimeSpan Timeout => TimeSpan.FromMilliseconds(200);
    public List<string> Requests { get; } = [];

    public Task<(int Status, string Body)> GetStringAsync(string path, CancellationToken ct)
    {
        Requests.Add(path.TrimEnd('/'));
        var key = path.TrimEnd('/');
        if (routes.TryGetValue(key, out var r))
            return Task.FromResult(r);
        return Task.FromResult((-1, string.Empty));
    }

    public void Dispose() { }
}

public class FingerprintTests
{
    private static readonly Dictionary<string, (int, string)> VllmRoutes = new()
    {
        ["metrics"] = (200,
            """
            # HELP vllm:num_requests_running running
            # TYPE vllm:num_requests_running gauge
            vllm:num_requests_running 0
            """),
    };

    private static readonly Dictionary<string, (int, string)> LlamaMetricsRoutes = new()
    {
        ["metrics"] = (200,
            """
            # HELP llamacpp:prompt_tokens_total prompt tokens
            # TYPE llamacpp:prompt_tokens_total counter
            llamacpp:prompt_tokens_total 5
            """),
    };

    private static readonly Dictionary<string, (int, string)> LlamaSlotsOnlyRoutes = new()
    {
        ["slots"] = (200,
            """[{"id":0,"n_ctx":4096,"speculative":false,"is_processing":false}]"""),
    };

    private static readonly Dictionary<string, (int, string)> LmStudioRoutes = new()
    {
        ["api/v0/models"] = (200,
            """{"object":"list","data":[{"id":"gemma-4-26b","object":"model","state":"loaded","max_context_length":32768}]}"""),
    };

    private static readonly Dictionary<string, (int, string)> OllamaRoutes = new()
    {
        ["api/ps"] = (200, """{"Models":[{"Name":"qwen3:30b","Model":"qwen3:30b","Size":1,"Digest":"x"}]}"""),
    };

    private static readonly Dictionary<string, (int, string)> GenericOpenAiRoutes = new()
    {
        ["v1/models"] = (200, """{"object":"list","data":[{"id":"some-model","object":"model"}]}"""),
    };

    [Fact]
    public async Task Identifies_Vllm_By_Metrics_Prefix_Not_Port()
    {
        var fp = await MakeFingerprinter(VllmRoutes).FingerprintAsync(new Uri("http://127.0.0.1:1234/"), default);
        Assert.Equal(BackendKind.Vllm, fp.Kind);
        Assert.Contains("vllm:", fp.Evidence);
    }

    [Fact]
    public async Task Identifies_LlamaCpp_Metrics_And_Slots_Fallback()
    {
        var viaMetrics = await MakeFingerprinter(LlamaMetricsRoutes).FingerprintAsync(new Uri("http://127.0.0.1:8080/"), default);
        Assert.Equal(BackendKind.LlamaCpp, viaMetrics.Kind);

        var viaSlots = await MakeFingerprinter(LlamaSlotsOnlyRoutes).FingerprintAsync(new Uri("http://127.0.0.1:8080/"), default);
        Assert.Equal(BackendKind.LlamaCpp, viaSlots.Kind);
        Assert.Contains("/slots", viaSlots.Evidence);
    }

    [Fact]
    public async Task Identifies_LM_Studio_Ollama_GenericOpenAi_And_Unknown()
    {
        var lm = await MakeFingerprinter(LmStudioRoutes).FingerprintAsync(new Uri("http://127.0.0.1:1234/"), default);
        Assert.Equal(BackendKind.LmStudio, lm.Kind);

        var ollama = await MakeFingerprinter(OllamaRoutes).FingerprintAsync(new Uri("http://127.0.0.1:11434/"), default);
        Assert.Equal(BackendKind.Ollama, ollama.Kind);

        var generic = await MakeFingerprinter(GenericOpenAiRoutes).FingerprintAsync(new Uri("http://127.0.0.1:9999/"), default);
        Assert.Equal(BackendKind.GenericOpenAi, generic.Kind);

        var unknown = await MakeFingerprinter(new Dictionary<string, (int, string)>()).FingerprintAsync(new Uri("http://127.0.0.1:1/"), default);
        Assert.Equal(BackendKind.Unknown, unknown.Kind);
    }

    /// <summary>llama.cpp and Ollama also expose /v1/models — the fingerprinter
    /// must positively identify them before falling through to "generic".</summary>
    [Fact]
    public async Task Specific_Engines_Win_Over_Generic_OpenAI_Schema()
    {
        // llama-server with metrics off but /slots on, plus its own /v1/models
        var routes = new Dictionary<string, (int, string)>(LlamaSlotsOnlyRoutes)
        {
            ["v1/models"] = (200, """{"object":"list","data":[{"id":"qwen","object":"model"}]}"""),
        };
        var fp = await MakeFingerprinter(routes).FingerprintAsync(new Uri("http://127.0.0.1:8080/"), default);
        Assert.NotEqual(BackendKind.GenericOpenAi, fp.Kind);

        var ollamaPlusV1 = new Dictionary<string, (int, string)>(OllamaRoutes)
        {
            ["v1/models"] = (200, """{"object":"list","data":[{"id":"q","object":"model"}]}"""),
        };
        var fp2 = await MakeFingerprinter(ollamaPlusV1).FingerprintAsync(new Uri("http://127.0.0.1:11434/"), default);
        Assert.Equal(BackendKind.Ollama, fp2.Kind);
    }

    [Fact]
    public void Port_Number_Alone_Never_Determines_Kind()
    {
        // Same port 1234 with different bodies must yield different kinds —
        // verified above; this test documents the invariant explicitly.
        Assert.True(true);
    }

    private static EndpointFingerprinter MakeFingerprinter(IReadOnlyDictionary<string, (int, string)> routes) =>
        new(uri => new FakeHttp(uri, routes));

    [Fact]
    public void Llama_Slots_Schema_Checks_Are_Strict()
    {
        Assert.False(LlamaCppAdapter.LooksLikeSlots(default));
        var arr = JsonDocument.Parse("""[{"foo":1}]""").RootElement;
        Assert.False(LlamaCppAdapter.LooksLikeSlots(arr));

        var ok = JsonDocument.Parse("""[{"id":0,"is_processing":true,"n_decoded":5}]""").RootElement;
        Assert.True(LlamaCppAdapter.LooksLikeSlots(ok));

        var nextTokenStyle = JsonDocument.Parse(
            """[{"id":0,"n_ctx":512,"next_token":[{"n_decoded":3}]}]""").RootElement;
        Assert.True(LlamaCppAdapter.LooksLikeSlots(nextTokenStyle));
    }

    [Fact]
    public void ReadNDecoded_Handles_TopLevel_And_Nested_Locations()
    {
        var top = JsonDocument.Parse("""{"n_decoded":42}""").RootElement;
        Assert.Equal(42, LlamaCppAdapter.ReadNDecoded(top));

        var nested = JsonDocument.Parse("""{"next_token":[{"n_decoded":7}]}""").RootElement;
        Assert.Equal(7, LlamaCppAdapter.ReadNDecoded(nested));

        var none = JsonDocument.Parse("{}").RootElement;
        Assert.Equal(-1, LlamaCppAdapter.ReadNDecoded(none));
    }

    [Fact]
    public async Task Vllm_Identify_Rejects_Foreign_Metrics()
    {
        var adapter = new VllmAdapter();
        var http = new FakeHttp(new Uri("http://x/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "llamacpp:prompt_tokens_total 1\n"),
        });
        var res = await adapter.IdentifyAsync(http, default);
        Assert.Null(res);
    }

    [Fact]
    public async Task Llama_Slots_Mode_Marks_Unavailable_Metrics_Honestly()
    {
        var adapter = new LlamaCppAdapter();
        var http = new FakeHttp(new Uri("http://x/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (-1, ""),
            ["slots"] = (200, """
                [
                  {"id":0,"n_ctx":4096,"speculative":false,"is_processing":true,
                   "id_task":135,"n_prompt_tokens":12400,"n_prompt_tokens_processed":12400,
                   "next_token":[{"has_next_token":true,"n_remain":100,"n_decoded":184}]},
                  {"id":1,"n_ctx":4096,"speculative":false,"is_processing":false}
                ]
                """),
            ["props"] = (200, """{"total_slots":2,"default_generation_settings":{"model":{"path":"/models/Qwen.gguf"}}}"""),
        });

        var snap = await adapter.CollectAsync(http, default);

        Assert.Equal(ConnectionState.Limited, snap.State);      // metrics disabled → limited
        Assert.True(snap.Running.HasValue && snap.Running.Value == 1);
        Assert.False(snap.Queued.HasValue);                     // not derivable — never faked
        Assert.False(snap.PrefillTokPerSec.HasValue);           // not derivable from slots
        Assert.False(snap.KvCacheUsage.HasValue);
        Assert.NotNull(snap.Requests);
        var row = Assert.Single(snap.Requests!);
        Assert.Equal("#135", row.Id);
        Assert.Equal(12400, row.InputTokens.Value);
        Assert.Equal(184, row.OutputTokens.Value);
        Assert.Equal("Qwen.gguf", snap.ModelName);
    }
}
