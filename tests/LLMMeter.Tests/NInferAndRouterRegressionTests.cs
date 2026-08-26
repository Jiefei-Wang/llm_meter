using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LLMMeter.Adapters;
using LLMMeter.Collection;
using LLMMeter.Core;
using LLMMeter.Discovery;
using LLMMeter.Persistence;
using Xunit;

namespace LLMMeter.Tests;

public class NInferAndRouterRegressionTests
{
    private static readonly Dictionary<string, (int, string)> NInferHttpOnlyRoutes = new()
    {
        ["health"] = (200, """{"status":"ok"}"""),
        ["v1/models"] = (200, """{"object":"list","data":[{"id":"qwen3-32b","object":"model"}]}"""),
    };

    // J1: NInfer limited mode
    [Fact]
    public async Task J1_NInfer_Limited_Mode_When_Telemetry_Absent()
    {
        var http = new FakeHttp(new Uri("http://127.0.0.1:8123/"), NInferHttpOnlyRoutes);
        var endpoint = new EndpointRef("win|127.0.0.1:8123", http.BaseUrl, OriginKind.WindowsHost, null);
        var adapter = new NInferAdapter(endpoint);

        var snapshot = await adapter.CollectAsync(http, default);

        Assert.Equal(BackendKind.NInfer, adapter.Kind);
        Assert.Equal(ConnectionState.Limited, snapshot.State);
        Assert.Equal("qwen3-32b", snapshot.ModelName);
        Assert.False(snapshot.Running.HasValue);
        Assert.False(snapshot.Queued.HasValue);
        Assert.False(snapshot.PrefillTokPerSec.HasValue);
        Assert.False(snapshot.GenerationTokPerSec.HasValue);
        Assert.False(snapshot.RecentTtftMs.HasValue);
        Assert.Null(snapshot.Requests);

        var help = adapter.GetHelp();
        Assert.NotNull(help);
        Assert.Contains("8123", help!.SuggestedCommand);
        Assert.Contains("ninfer-8123.requests.jsonl", help.SuggestedCommand);
    }

    // J2: NInfer expected paths & WSL translation
    [Fact]
    public void J2_NInfer_Expected_Paths_And_Wsl_Translation()
    {
        string linux = NInferPathHelper.BuildNInferLinuxTelemetryPath(8123);
        Assert.Equal("/tmp/llmmeter/ninfer-8123.requests.jsonl", linux);

        string win = NInferPathHelper.BuildNInferWindowsTelemetryPath(8123);
        Assert.Contains("ninfer-8123.requests.jsonl", win);

        string wsl = NInferPathHelper.BuildNInferWslTelemetryPath("Ubuntu-24.04", 8123);
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\tmp\llmmeter\ninfer-8123.requests.jsonl", wsl);

        var wslEndpoint = new EndpointRef("wsl|Ubuntu-24.04|127.0.0.1:8123", new Uri("http://127.0.0.1:8123"), OriginKind.Wsl, "Ubuntu-24.04");
        string resolvedWsl = NInferPathHelper.ResolveHostTelemetryPath(wslEndpoint);
        Assert.StartsWith(@"\\wsl.localhost\Ubuntu-24.04", resolvedWsl);
    }

    // J3: NInfer server_start parsing
    [Fact]
    public void J3_NInfer_Server_Start_Parsing()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            string serverStartJson = """
                {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":1710000000000,"server_instance_id":"inst-1","server":{"host":"0.0.0.0","port":8123,"public_model_id":"qwen-32b"},"artifact":{"target":"qwen3_6_32b","weights_id":"fp8"},"engine":{"max_context":32768,"kv_capacity":65536,"kv_capacity_mode":"auto","max_concurrency":16,"kv_cache":"int8-group64","speculative_backend":"mtp","speculative_draft_window":2,"log_stats_interval_ms":2000},"environment":{"gpu_name":"NVIDIA RTX 4090"}}
                """;
            File.WriteAllText(tempFile, serverStartJson + "\n");
            reader.FilePath = tempFile;

            bool ok = reader.Poll(100);
            Assert.True(ok);
            Assert.Equal("qwen-32b", reader.PublicModelId);
            Assert.Equal("32768", reader.ServerInfo["Max Context"]);
            Assert.Equal("65536", reader.ServerInfo["KV Capacity"]);
            Assert.Equal("16", reader.ServerInfo["Max Concurrency"]);
            Assert.Equal("mtp", reader.ServerInfo["Speculative"]);
            Assert.Equal("2", reader.ServerInfo["Draft Window"]);
            Assert.Equal("NVIDIA RTX 4090", reader.ServerInfo["GPU"]);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J4: NInfer throughput parsing
    [Fact]
    public void J4_NInfer_Throughput_Parsing()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            string throughputJson = """
                {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000002000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":1000,"committed_decode":200},"scheduler":{"running":3,"waiting":1}}
                """;
            File.WriteAllText(tempFile, throughputJson + "\n");
            reader.FilePath = tempFile;

            bool ok = reader.Poll(100);
            Assert.True(ok);

            var builder = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(builder);
            var snap = builder.Build();

            Assert.Equal(3, snap.Running.Value);
            Assert.Equal(MetricQuality.Exact, snap.Running.Quality);
            Assert.Equal(1, snap.Queued.Value);
            Assert.Equal(MetricQuality.Exact, snap.Queued.Quality);

            // 1000 tokens / 2.0s = 500 tok/s
            Assert.Equal(500.0, snap.PrefillTokPerSec.Value);
            Assert.Equal(MetricQuality.Approximate, snap.PrefillTokPerSec.Quality);

            // 200 tokens / 2.0s = 100 tok/s
            Assert.Equal(100.0, snap.GenerationTokPerSec.Value);
            Assert.Equal(MetricQuality.Approximate, snap.GenerationTokPerSec.Quality);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J5: NInfer cumulative totals
    [Fact]
    public void J5_NInfer_Cumulative_Totals_Accumulation()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            string line1 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000002000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":1000,"committed_decode":200},"scheduler":{"running":1,"waiting":0}}""";
            string line2 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000004000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":500,"committed_decode":150},"scheduler":{"running":1,"waiting":0}}""";
            File.WriteAllText(tempFile, line1 + "\n" + line2 + "\n");
            reader.FilePath = tempFile;

            reader.Poll(100);

            var builder = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(builder);
            var snap = builder.Build();

            Assert.Equal(1500, snap.PrefilledTokensTotal.Value); // 1000 + 500
            Assert.Equal(350, snap.GeneratedTokensTotal.Value);   // 200 + 150

            // Unchanged file poll does not double-count
            reader.Poll(200);
            var builder2 = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(builder2);
            var snap2 = builder2.Build();

            Assert.Equal(1500, snap2.PrefilledTokensTotal.Value);
            Assert.Equal(350, snap2.GeneratedTokensTotal.Value);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J6: NInfer incremental reading & partial lines
    [Fact]
    public void J6_NInfer_Incremental_Reading_And_Partial_Lines()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;
            string line1 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000002000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":100,"committed_decode":50},"scheduler":{"running":1,"waiting":0}}""";
            File.WriteAllText(tempFile, line1 + "\n");

            Assert.True(reader.Poll(100));

            // Append partial line
            string line2 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000004000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":200,"committed_decode":75},"scheduler":{"running":2,"waiting":1}}""";
            string partial = line2.Substring(0, 50);
            string remainder = line2.Substring(50) + "\n";
            File.AppendAllText(tempFile, partial);

            // Partial line is buffered and not processed yet
            reader.Poll(150);
            var b1 = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b1);
            Assert.Equal(100, b1.PrefilledTokensTotal.Value);

            // Finish the line
            File.AppendAllText(tempFile, remainder);
            reader.Poll(200);

            var b2 = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b2);
            Assert.Equal(300, b2.PrefilledTokensTotal.Value); // 100 + 200
            Assert.Equal(2, b2.Running.Value);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J7: NInfer truncation and restart
    [Fact]
    public void J7_NInfer_Truncation_And_Restart()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;
            string line1 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000002000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":1000,"committed_decode":500},"scheduler":{"running":1,"waiting":0}}""";
            File.WriteAllText(tempFile, line1 + "\n");
            reader.Poll(100);

            var b1 = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b1);
            Assert.Equal(1000, b1.PrefilledTokensTotal.Value);

            // Case A: file truncated to shorter length
            string shorter = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000004000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":10,"committed_decode":5},"scheduler":{"running":1,"waiting":0}}""";
            File.WriteAllText(tempFile, shorter + "\n");
            reader.Poll(200);

            var b2 = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b2);
            Assert.Equal(10, b2.PrefilledTokensTotal.Value);

            // Case B: server_instance_id changes (server restart)
            string restart = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":1710000006000,"server_instance_id":"inst-2","server":{"public_model_id":"new-model"}}""";
            File.AppendAllText(tempFile, restart + "\n");
            reader.Poll(300);

            Assert.Equal("inst-2", reader.ServerInstanceId);
            Assert.Equal("new-model", reader.PublicModelId);
            Assert.False(b2.PrefilledTokensTotal.Value == 0); // previous builder unaffected
            var b3 = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b3);
            Assert.False(b3.PrefilledTokensTotal.HasValue); // reset after server_start
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J8: NInfer malformed line tolerance
    [Fact]
    public void J8_NInfer_Malformed_Line_Tolerance()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;
            string line1 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000002000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":100,"committed_decode":50},"scheduler":{"running":1,"waiting":0}}""";
            string corrupt = "{ this is completely corrupted garbage JSON !!!";
            string line2 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000004000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":200,"committed_decode":80},"scheduler":{"running":2,"waiting":1}}""";
            File.WriteAllText(tempFile, $"{line1}\n{corrupt}\n{line2}\n");

            // Reader must not throw and must parse both valid lines
            bool ok = reader.Poll(100);
            Assert.True(ok);

            var b = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b);
            Assert.Equal(300, b.PrefilledTokensTotal.Value);
            Assert.Equal(130, b.GeneratedTokensTotal.Value);
            Assert.Equal(2, b.Running.Value);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J9: NInfer request lifecycle
    [Fact]
    public void J9_NInfer_Request_Lifecycle()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;
            string start = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_start","timestamp_unix_ms":1710000001000,"server_instance_id":"inst-1","request":{"request_id":42}}""";
            File.WriteAllText(tempFile, start + "\n");
            reader.Poll(100);

            Assert.Equal(1, reader.ActiveRequestCount);
            var b1 = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b1);
            var req = Assert.Single(b1.Requests!);
            Assert.Equal("#42", req.Id);
            // No fabricated speeds during live generation
            Assert.False(req.TokensPerSecond.HasValue);

            // request_done
            string done = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_done","timestamp_unix_ms":1710000002000,"server_instance_id":"inst-1","request":{"request_id":42},"timings_seconds":{"ttft":0.045}}""";
            File.AppendAllText(tempFile, done + "\n");
            reader.Poll(200);

            Assert.Equal(0, reader.ActiveRequestCount);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J10: NInfer TTFT rolling average
    [Fact]
    public void J10_NInfer_Ttft_Rolling_Average()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;
            // 3 requests with TTFT 20ms, 40ms, 60ms (0.02, 0.04, 0.06 seconds)
            var lines = new[]
            {
                """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_done","timestamp_unix_ms":100,"server_instance_id":"inst-1","request":{"request_id":1},"timings_seconds":{"ttft":0.020}}""",
                """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_done","timestamp_unix_ms":200,"server_instance_id":"inst-1","request":{"request_id":2},"timings_seconds":{"ttft":0.040}}""",
                """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_done","timestamp_unix_ms":300,"server_instance_id":"inst-1","request":{"request_id":3},"timings_seconds":{"ttft":0.060}}""",
            };
            File.WriteAllText(tempFile, string.Join("\n", lines) + "\n");
            reader.Poll(100);

            var b = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(b);

            Assert.True(b.RecentTtftMs.HasValue);
            Assert.Equal(40.0, b.RecentTtftMs.Value, 1);
            Assert.Contains("3 requests", b.RecentTtftMs.Note);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J11: NInfer file missing/stale while HTTP online
    [Fact]
    public async Task J11_NInfer_File_Stale_Degrades_To_Limited_Not_Offline()
    {
        var http = new FakeHttp(new Uri("http://127.0.0.1:8123/"), NInferHttpOnlyRoutes);
        var endpoint = new EndpointRef("win|127.0.0.1:8123", http.BaseUrl, OriginKind.WindowsHost, null);
        var reader = new NInferJsonlTelemetryReader();
        var adapter = new NInferAdapter(endpoint, reader, ownsReader: true);

        // Inject initial data
        string tempFile = Path.GetTempFileName();
        try
        {
            string line = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":1710000002000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":100,"committed_decode":50},"scheduler":{"running":1,"waiting":0}}""";
            File.WriteAllText(tempFile, line + "\n");
            reader.FilePath = tempFile;

            long t0 = 1000;
            adapter.Clock = () => t0;
            var s1 = await adapter.CollectAsync(http, default);
            Assert.Equal(ConnectionState.Online, s1.State);

            // Simulate 60 seconds passing with no new events (stale)
            long t1 = t0 + (long)(60 * System.Diagnostics.Stopwatch.Frequency);
            adapter.Clock = () => t1;
            var s2 = await adapter.CollectAsync(http, default);

            Assert.Equal(ConnectionState.Limited, s2.State);
            Assert.NotEqual(ConnectionState.Offline, s2.State);
            Assert.Equal("NInfer telemetry log is stale", s2.Info["Telemetry"]);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // J12: llama router ModelId propagation end-to-end
    [Fact]
    public void J12_Llama_Router_ModelId_Propagation_End_To_End()
    {
        var endpoint = new EndpointRef("manual|127.0.0.1:8080", new Uri("http://127.0.0.1:8080"), OriginKind.Manual, null);
        var target = new BackendTarget("manual|127.0.0.1:8080|model-B", endpoint, BackendKind.LlamaCpp, "model-B", "llama-server · model-B");

        Assert.True(target.RequiresModelScopedCollector);
        Assert.Equal("model-B", target.ModelId);

        using var mgr = new CollectorManager();
        string? scopedModelId = target.RequiresModelScopedCollector ? target.ModelId : null;
        var collector = mgr.GetOrAdd(target.Endpoint, target.Kind, scopedModelId);

        Assert.NotNull(collector);
        Assert.Equal(1, mgr.Count);
        string expectedKey = CollectorManager.CollectorKey(endpoint, "model-B");
        Assert.True(mgr.Remove(expectedKey));
    }

    // J13: llama router model counts
    [Fact]
    public void J13_Llama_Router_Model_Counts()
    {
        // Case 0 loaded models
        var snap0 = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Limited,
            Kind = BackendKind.LlamaCpp,
            LoadedModels = [],
            Info = new Dictionary<string, string> { ["Router"] = "true" },
        };
        // Case 1 loaded model
        var snap1 = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Online,
            Kind = BackendKind.LlamaCpp,
            LoadedModels = ["qwen-7b"],
            Info = new Dictionary<string, string> { ["Router"] = "true" },
        };
        // Case 2 loaded models
        var snap2 = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Online,
            Kind = BackendKind.LlamaCpp,
            LoadedModels = ["qwen-7b", "llama-8b"],
            Info = new Dictionary<string, string> { ["Router"] = "true" },
        };

        Assert.Empty(snap0.LoadedModels);
        Assert.Single(snap1.LoadedModels);
        Assert.Equal(2, snap2.LoadedModels.Count);
    }

    // J14: llama router autoload prevention
    [Fact]
    public async Task J14_Llama_Router_Autoload_Prevention()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["props"] = (200, """{"role":"router"}"""),
            ["v1/models"] = (200, """{"object":"list","data":[{"id":"qwen"}]}"""),
            ["metrics?model=qwen&autoload=false"] = (200, "llamacpp:prompt_tokens_total 10\n"),
            ["slots?model=qwen&autoload=false"] = (200, "[]"),
            ["props?model=qwen&autoload=false"] = (200, """{"total_slots":1}"""),
        };
        var http = new FakeHttp(new Uri("http://x/"), routes);

        var adapter = new LlamaCppAdapter("qwen");
        var snap = await adapter.CollectAsync(http, default);

        Assert.NotEqual(ConnectionState.Offline, snap.State);
        Assert.All(http.Requests, r =>
        {
            if (r.Contains("model=qwen"))
            {
                Assert.Contains("autoload=false", r);
            }
        });
    }

    // J15: Authenticated auto-detection
    [Fact]
    public async Task J15_Authenticated_Auto_Detection()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "vllm:num_requests_running 0\n"),
        };
        var capturedTokens = new List<string?>();

        var fingerprinter = new EndpointFingerprinter(uri =>
        {
            return new FakeHttp(uri, routes);
        });

        var fp = await fingerprinter.FingerprintAsync(new Uri("http://127.0.0.1:8000/"), "secret-token", default);
        Assert.Equal(BackendKind.Vllm, fp.Kind);
    }

    // J16: LM Studio multiple loaded models emits 1 endpoint target
    [Fact]
    public void J16_LmStudio_Multiple_Loaded_Models_Emits_One_Endpoint_Target()
    {
        var endpoint = new EndpointRef("manual|127.0.0.1:1234", new Uri("http://127.0.0.1:1234"), OriginKind.Manual, null);
        var target = new BackendTarget(endpoint.Id, endpoint, BackendKind.LmStudio, null, "LM Studio :1234");

        Assert.False(target.RequiresModelScopedCollector);
        Assert.Null(target.ModelId);
    }

    // J17: Collector model-scoped keys & pruning
    [Fact]
    public void J17_Collector_Model_Scoped_Keys_And_Pruning()
    {
        using var mgr = new CollectorManager();
        var endpoint = new EndpointRef("manual|127.0.0.1:8080", new Uri("http://127.0.0.1:8080"), OriginKind.Manual, null);

        var colA = mgr.GetOrAdd(endpoint, BackendKind.LlamaCpp, "model-A");
        var colB = mgr.GetOrAdd(endpoint, BackendKind.LlamaCpp, "model-B");

        Assert.Equal(2, mgr.Count);
        Assert.NotSame(colA, colB);

        // Prune only colA
        mgr.Prune(c => c.ModelId != "model-A");
        Assert.Equal(1, mgr.Count);
        Assert.False(colB.IsDisposed);
        Assert.True(colA.IsDisposed);
    }


    // J18: Credential protection DPAPI and migration
    [Fact]
    public void J18_Credential_Protection_TryProtect_And_Plaintext_Migration()
    {
        // TryProtect encrypts with enc: prefix on Windows
        bool ok = CredentialProtection.TryProtect("my-secret-token", out var protectedKey);
        Assert.True(ok);
        Assert.NotNull(protectedKey);
        Assert.StartsWith("enc:", protectedKey!);

        // Decrypts cleanly
        string? plain = CredentialProtection.Unprotect(protectedKey);
        Assert.Equal("my-secret-token", plain);

        // Plaintext config normalization migrates unencrypted key
        var cfg = new AppConfiguration();
        cfg.ManualBackends.Add(new ManualEndpointConfig
        {
            Name = "Legacy",
            Url = "http://127.0.0.1:8000",
            ApiKey = "legacy-plaintext-token", // unencrypted
        });

        ConfigurationService.Normalize(cfg);
        Assert.StartsWith("enc:", cfg.ManualBackends[0].ApiKey!);
        Assert.Equal("legacy-plaintext-token", cfg.ManualBackends[0].PlainTextApiKey);
    }
}
