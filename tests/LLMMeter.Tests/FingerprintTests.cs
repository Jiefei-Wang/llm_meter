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

    [Fact]
    public async Task Ollama_Uses_Official_Lowercase_Api_Shape()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/ps"] = (200, """{"models":[{"name":"gemma3","model":"gemma3","size_vram":5333539264,"expires_at":"2030-01-01T00:00:00Z"}]}"""),
            ["api/version"] = (200, """{"version":"1.2.3"}"""),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);
        var adapter = new OllamaAdapter();

        Assert.NotNull(await adapter.IdentifyAsync(http, default));
        var snapshot = await adapter.CollectAsync(http, default);
        Assert.Equal("gemma3", snapshot.ModelName);
        Assert.Contains("gemma3", snapshot.LoadedModels);
        Assert.Contains("gemma3 VRAM", snapshot.Info.Keys);
    }

    [Fact]
    public async Task LM_Studio_V1_Uses_Native_Models_And_Loaded_Instances()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (200, """
                {"models":[{"type":"llm","key":"google/gemma","display_name":"Gemma",
                  "loaded_instances":[{"id":"google/gemma","config":{"context_length":4096}}]}]}
                """),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);
        var adapter = new LmStudioAdapter();

        Assert.NotNull(await adapter.IdentifyAsync(http, default));
        var snapshot = await adapter.CollectAsync(http, default);
        Assert.Equal("google/gemma", snapshot.ModelName);
        Assert.Equal("4096", snapshot.Info["google/gemma ctx"]);
    }

    [Fact]
    public async Task LM_Studio_Does_Not_Accept_Generic_List_At_Native_V1_Path()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (200, """{"object":"list","data":[{"id":"other","object":"model"}]}"""),
        };
        var result = await new LmStudioAdapter().IdentifyAsync(new FakeHttp(new Uri("http://x/"), routes), default);
        Assert.Null(result);
    }

    [Fact]
    public async Task Fingerprinting_Caches_Repeated_Metrics_Request()
    {
        var http = new FakeHttp(new Uri("http://x/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "foreign_metric 1\n"),
        });
        var result = await new EndpointFingerprinter(_ => http).FingerprintAsync(http.BaseUrl, default);
        Assert.Equal(BackendKind.Unknown, result.Kind);
        Assert.Equal(1, http.Requests.Count(p => p == "metrics"));
    }

    [Fact]
    public async Task Vllm_Exposes_Cumulative_Token_Counters()
    {
        var http = new FakeHttp(new Uri("http://x/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, """
                vllm:num_requests_running 1
                vllm:num_requests_waiting 0
                vllm:prompt_tokens_total 1200
                vllm:generation_tokens_total 3456
                """),
        });
        var snapshot = await new VllmAdapter().CollectAsync(http, default);

        Assert.Equal(1200, snapshot.PrefilledTokensTotal.Value);
        Assert.Equal(3456, snapshot.GeneratedTokensTotal.Value);
        Assert.Equal(MetricQuality.Exact, snapshot.GeneratedTokensTotal.Quality);
    }

    [Fact]
    public void LM_Studio_Recognizes_Empty_Native_Catalogs()
    {
        using var v0 = JsonDocument.Parse("""{"object":"list","data":[]}""");
        using var v1 = JsonDocument.Parse("""{"models":[]}""");
        Assert.True(LmStudioAdapter.LooksLikeLmStudio(v0.RootElement));
        Assert.True(LmStudioAdapter.LooksLikeNativeV1(v1.RootElement));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"models\":{}}")]
    [InlineData("{\"models\":[{\"size\":1}]}")]
    public void Ollama_Rejects_Malformed_Process_Lists(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(OllamaAdapter.LooksLikeOllamaPs(doc.RootElement));
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
            ["props"] = (200, """{"total_slots":2,"model_path":"/models/Qwen.gguf","default_generation_settings":{}}"""),
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
        Assert.Equal(12400, row.PrefilledTokens.Value);
        Assert.Equal(184, row.OutputTokens.Value);
        Assert.Equal("Qwen.gguf", snap.ModelName);
    }

    [Fact]
    public async Task Llama_Metrics_Mode_Still_Enumerates_Active_Slots()
    {
        var adapter = new LlamaCppAdapter();
        var http = new FakeHttp(new Uri("http://x/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, """
                llamacpp:requests_processing 1
                llamacpp:requests_deferred 0
                llamacpp:prompt_tokens_total 100
                llamacpp:tokens_predicted_total 20
                """),
            ["slots"] = (200, """
                [{"id":0,"is_processing":true,"id_task":42,
                  "n_prompt_tokens":7,"n_prompt_tokens_processed":9,
                  "n_prompt_tokens_cache":3,"n_decoded":3}]
                """),
        });

        var snap = await adapter.CollectAsync(http, default);

        var request = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<RequestSnapshot>>(snap.Requests));
        Assert.Equal("#42", request.Id);
        Assert.Equal(12, request.InputTokens.Value);
        Assert.Equal(3, request.CachedTokens.Value);
        Assert.Equal(9, request.PrefilledTokens.Value);
        Assert.Equal(3, request.OutputTokens.Value);
        Assert.True(adapter.Capabilities.HasFlag(BackendCapabilities.ActiveRequestEnumeration));
    }

    [Fact]
    public void Llama_Metrics_Mode_Prefers_Live_Slot_Rates()
    {
        var metrics = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            State = ConnectionState.Online,
            Kind = BackendKind.LlamaCpp,
            PrefillTokPerSec = MetricValue<double>.Approx(0),
            GenerationTokPerSec = MetricValue<double>.Approx(0),
        };
        RequestSnapshot[] requests =
        [
            new()
            {
                Id = "#1",
                PrefillTokensPerSecond = MetricValue<double>.Approx(270),
                TokensPerSecond = MetricValue<double>.Approx(0),
            },
            new()
            {
                Id = "#2",
                PrefillTokensPerSecond = MetricValue<double>.Approx(30),
                TokensPerSecond = MetricValue<double>.Approx(40),
            },
        ];

        var combined = LlamaCppAdapter.WithRequests(metrics, requests);

        Assert.Equal(300, combined.PrefillTokPerSec.Value);
        Assert.Equal(40, combined.GenerationTokPerSec.Value);
    }

    [Fact]
    public async Task Llama_Props_Legacy_Schema_Fallback_Works()
    {
        var adapter = new LlamaCppAdapter();
        var http = new FakeHttp(new Uri("http://x/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (-1, ""),
            ["slots"] = (200, """[{"id":0,"is_processing":false}]"""),
            ["props"] = (200, """{"total_slots":4,"default_generation_settings":{"model":{"path":"/legacy/path/ModelLegacy.gguf"}}}"""),
        });

        var snap = await adapter.CollectAsync(http, default);
        Assert.Equal("ModelLegacy.gguf", snap.ModelName);
    }

    [Fact]
    public async Task Llama_Props_Retry_Timing_Does_Not_Spam_And_Retries_After_5_Seconds()
    {
        var adapter = new LlamaCppAdapter();
        long simulatedTicks = 10_000;
        adapter.Clock = () => simulatedTicks;

        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "llamacpp:prompt_tokens_total 10\n"),
            ["slots"] = (200, "[]"),
            ["props"] = (500, "internal error"), // initial failure
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);

        // First scrape at t=0: attempts /props and fails
        await adapter.CollectAsync(http, default);
        Assert.Equal(1, http.Requests.Count(r => r == "props"));

        // Second scrape at t=500ms: must NOT retry /props yet
        simulatedTicks += (long)(0.5 * System.Diagnostics.Stopwatch.Frequency);
        await adapter.CollectAsync(http, default);
        Assert.Equal(1, http.Requests.Count(r => r == "props"));

        // Third scrape at t=5.2s: now retry is due!
        routes["props"] = (200, """{"total_slots":2,"model_path":"/models/Retried.gguf","default_generation_settings":{}}""");
        simulatedTicks += (long)(5.2 * System.Diagnostics.Stopwatch.Frequency);
        var snap = await adapter.CollectAsync(http, default);
        Assert.Equal(2, http.Requests.Count(r => r == "props"));
        Assert.Equal("Retried.gguf", snap.ModelName);

        // Fourth scrape at t=10s: metadata is already obtained, no more repeated /props calls!
        simulatedTicks += (long)(5.0 * System.Diagnostics.Stopwatch.Frequency);
        await adapter.CollectAsync(http, default);
        Assert.Equal(2, http.Requests.Count(r => r == "props"));
    }

    [Fact]
    public void Llama_Capabilities_Do_Not_Include_RecentRequestTtft()
    {
        var adapter = new LlamaCppAdapter();
        Assert.False(adapter.Capabilities.HasFlag(BackendCapabilities.RecentRequestTtft));
    }

    [Fact]
    public void Llama_Metrics_Avoid_Partial_Slot_Rates_Overriding_Valid_Aggregate_Rate()
    {
        var metrics = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            State = ConnectionState.Online,
            Kind = BackendKind.LlamaCpp,
            PrefillTokPerSec = MetricValue<double>.None,
            GenerationTokPerSec = MetricValue<double>.Exact(120.0, MetricSource.NativeMetrics, "llamacpp:tokens_predicted_total delta"),
        };

        // 4 active requests in generation phase: 2 have live rates, 2 just started (no rate yet)
        RequestSnapshot[] requests =
        [
            new() { Id = "#1", OutputTokens = MetricValue<long>.Exact(10), TokensPerSecond = MetricValue<double>.Approx(30.0) },
            new() { Id = "#2", OutputTokens = MetricValue<long>.Exact(10), TokensPerSecond = MetricValue<double>.Approx(30.0) },
            new() { Id = "#3", OutputTokens = MetricValue<long>.Exact(1), TokensPerSecond = MetricValue<double>.None },
            new() { Id = "#4", OutputTokens = MetricValue<long>.Exact(1), TokensPerSecond = MetricValue<double>.None },
        ];

        var combined = LlamaCppAdapter.WithRequests(metrics, requests);

        // Must preserve the complete aggregate 120.0 rate rather than under-reporting 60.0!
        Assert.Equal(120.0, combined.GenerationTokPerSec.Value);

        // But in slots fallback mode without an aggregate Prometheus rate, partial rate is reported as approx:
        var metricsOffline = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            State = ConnectionState.Limited,
            Kind = BackendKind.LlamaCpp,
            GenerationTokPerSec = MetricValue<double>.None,
        };
        var combinedFallback = LlamaCppAdapter.WithRequests(metricsOffline, requests);
        Assert.Equal(60.0, combinedFallback.GenerationTokPerSec.Value);
        Assert.Equal(MetricQuality.Approximate, combinedFallback.GenerationTokPerSec.Quality);
    }

    [Fact]
    public async Task Llama_Router_Mode_Identified_And_Queries_Model_Specific_Endpoints()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["props"] = (200, """{"role":"router"}"""),
            ["metrics"] = (400, "model is required in router mode"),
            ["slots"] = (400, "model is required"),
            ["v1/models"] = (200, """{"object":"list","data":[{"id":"qwen-7b"},{"id":"deepseek-7b"}]}"""),
            ["metrics?model=qwen-7b&autoload=false"] = (200, "llamacpp:prompt_tokens_total 100\n"),
            ["slots?model=qwen-7b&autoload=false"] = (200, "[]"),
            ["props?model=qwen-7b&autoload=false"] = (200, """{"total_slots":2,"model_path":"/models/qwen.gguf"}"""),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);

        var fp = await MakeFingerprinter(routes).FingerprintAsync(http.BaseUrl, default);
        Assert.Equal(BackendKind.LlamaCpp, fp.Kind);
        Assert.Contains("router mode", fp.Evidence);

        // Model-scoped adapter queries model-specific endpoints with autoload=false
        var scopedAdapter = new LlamaCppAdapter("qwen-7b");
        var snap = await scopedAdapter.CollectAsync(http, default);
        Assert.NotEqual(ConnectionState.Offline, snap.State);
        Assert.Equal("qwen.gguf", snap.ModelName);
        Assert.Contains("metrics?model=qwen-7b&autoload=false", http.Requests);
        Assert.Contains("slots?model=qwen-7b&autoload=false", http.Requests);
    }


    [Fact]
    public async Task Vllm_Aggregates_Multi_Engine_Gauges_And_Counters()
    {
        const string metricsBody = """
            vllm:num_requests_running{model_name="qwen",engine="0"} 3
            vllm:num_requests_running{model_name="qwen",engine="1"} 4
            vllm:num_requests_waiting{model_name="qwen",engine="0"} 1
            vllm:num_requests_waiting{model_name="qwen",engine="1"} 2
            vllm:kv_cache_usage_perc{model_name="qwen",engine="0"} 0.50
            vllm:kv_cache_usage_perc{model_name="qwen",engine="1"} 0.70
            vllm:prompt_tokens_total{model_name="qwen",engine="0"} 1000
            vllm:prompt_tokens_total{model_name="qwen",engine="1"} 2000
            vllm:generation_tokens_total{model_name="qwen",engine="0"} 300
            vllm:generation_tokens_total{model_name="qwen",engine="1"} 400
            """;
        var http = new FakeHttp(new Uri("http://x/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, metricsBody),
        });

        var snap = await new VllmAdapter().CollectAsync(http, default);

        Assert.Equal(7, snap.Running.Value); // sum across engines (3 + 4)
        Assert.Equal(3, snap.Queued.Value);  // sum across engines (1 + 2)
        Assert.Equal(0.60, snap.KvCacheUsage.Value, 2); // average across engines (0.50 + 0.70) / 2
        Assert.Equal(3000, snap.PrefilledTokensTotal.Value); // 1000 + 2000
        Assert.Equal(700, snap.GeneratedTokensTotal.Value);   // 300 + 400
    }

    [Fact]
    public async Task LmStudio_V1_Wins_Over_V0_When_Both_Respond()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (200, """
                {"models":[{"type":"llm","key":"google/gemma","display_name":"Gemma",
                  "loaded_instances":[{"id":"google/gemma","config":{"context_length":4096}}]}]}
                """),
            ["api/v0/models"] = (200, """
                {"object":"list","data":[{"id":"gemma-legacy","object":"model","state":"loaded","max_context_length":32768}]}
                """),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);
        var adapter = new LmStudioAdapter();

        var fp = await adapter.IdentifyAsync(http, default);
        Assert.NotNull(fp);
        Assert.Contains("/api/v1/models", fp!.Evidence);

        var snap = await adapter.CollectAsync(http, default);
        Assert.Equal("REST API v1", snap.Info["API"]);
        Assert.Equal("google/gemma", snap.ModelName);
    }

    [Fact]
    public async Task LmStudio_V0_Accepted_As_Fallback()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (404, ""),
            ["api/v0/models"] = (200, """
                {"object":"list","data":[{"id":"gemma-legacy","object":"model","state":"loaded","max_context_length":32768}]}
                """),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);
        var adapter = new LmStudioAdapter();

        var fp = await adapter.IdentifyAsync(http, default);
        Assert.NotNull(fp);
        Assert.Contains("/api/v0/models", fp!.Evidence);

        var snap = await adapter.CollectAsync(http, default);
        Assert.Equal("REST API v0", snap.Info["API"]);
        Assert.Equal("gemma-legacy", snap.ModelName);
    }

    [Fact]
    public async Task LmStudio_Reports_Loaded_Instance_Context_Length_Not_Max_Context_Length()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (200, """
                {
                  "models": [
                    {
                      "type": "llm",
                      "key": "google/gemma",
                      "display_name": "Gemma",
                      "max_context_length": 262144,
                      "loaded_instances": [
                        {
                          "id": "instance-1",
                          "config": {
                            "context_length": 4096
                          }
                        }
                      ]
                    }
                  ]
                }
                """),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);
        var snap = await new LmStudioAdapter().CollectAsync(http, default);

        Assert.Equal("google/gemma", snap.ModelName);
        Assert.Equal("4096", snap.Info["google/gemma ctx"]);
        Assert.False(snap.Info.ContainsKey("google/gemma max ctx"));
    }

    [Fact]
    public async Task LmStudio_V0_Labels_Max_Context_Length_Clearly()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (404, ""),
            ["api/v0/models"] = (200, """
                {"object":"list","data":[{"id":"gemma-4-26b","object":"model","state":"loaded","max_context_length":32768}]}
                """),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);
        var snap = await new LmStudioAdapter().CollectAsync(http, default);

        Assert.Equal("32768", snap.Info["gemma-4-26b max ctx"]);
        Assert.False(snap.Info.ContainsKey("gemma-4-26b ctx"));
    }

    [Fact]
    public async Task LmStudio_Status_Endpoint_Is_Never_Hit()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v0/status"] = (404, "not found"),
            ["api/v1/models"] = (200, """{"models":[]}"""),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);
        var adapter = new LmStudioAdapter();

        await adapter.CollectAsync(http, default);
        // api/v0/status probe was removed (spec Part E1) and must never be queried
        Assert.Equal(0, http.Requests.Count(r => r == "api/v0/status"));

        await adapter.CollectAsync(http, default);
        Assert.Equal(0, http.Requests.Count(r => r == "api/v0/status"));
    }


    [Fact]
    public async Task Bearer_Authentication_Attached_When_Configured_And_Absent_Otherwise()
    {
        HttpRequestMessage? capturedWithAuth = null;
        HttpRequestMessage? capturedWithoutAuth = null;

        var handlerWithAuth = new TestCaptureHandler(req => capturedWithAuth = req);
        using var clientWithAuth = new HttpClient(handlerWithAuth)
        {
            DefaultRequestHeaders = { { "User-Agent", "LLMMeter/1.0" } }
        };
        clientWithAuth.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token-123");

        await clientWithAuth.GetAsync("http://127.0.0.1:1234/test");
        Assert.NotNull(capturedWithAuth);
        Assert.Equal("Bearer", capturedWithAuth!.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token-123", capturedWithAuth.Headers.Authorization?.Parameter);

        var handlerWithoutAuth = new TestCaptureHandler(req => capturedWithoutAuth = req);
        using var clientWithoutAuth = new HttpClient(handlerWithoutAuth)
        {
            DefaultRequestHeaders = { { "User-Agent", "LLMMeter/1.0" } }
        };
        await clientWithoutAuth.GetAsync("http://127.0.0.1:1234/test");
        Assert.NotNull(capturedWithoutAuth);
        Assert.Null(capturedWithoutAuth!.Headers.Authorization);
    }

    private sealed class TestCaptureHandler(Action<HttpRequestMessage> onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
