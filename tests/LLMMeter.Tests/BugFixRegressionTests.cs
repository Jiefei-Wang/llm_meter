using System.IO;
using System.Text.Json;
using LLMMeter.Adapters;
using LLMMeter.Collection;
using LLMMeter.Core;
using LLMMeter.Discovery;
using LLMMeter.Persistence;
using LLMMeter.UI;
using Xunit;

namespace LLMMeter.Tests;

public class BugFixRegressionTests
{
    // =========================================================================
    // BUG #1: llama.cpp router /models status & schema parsing
    // =========================================================================

    [Fact]
    public async Task Bug1_LlamaRouter_ShapeA_TopLevelArray()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["models"] = (200, """[{"id":"model-1","status":"loaded"},{"id":"model-2","status":"unloaded"}]"""),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8080/"), routes);

        var detailed = await LlamaCppAdapter.EnumerateModelsDetailedAsync(http, default);
        var loaded = await LlamaCppAdapter.EnumerateModelsAsync(http, default);

        Assert.Equal(2, detailed.Count);
        Assert.Equal("loaded", detailed[0].Status);
        Assert.Equal("unloaded", detailed[1].Status);
        Assert.Single(loaded);
        Assert.Equal("model-1", loaded[0]);
    }

    [Fact]
    public async Task Bug1_LlamaRouter_ShapeB_ModelsObject()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["models"] = (200, """{"models":[{"name":"qwen","status":"loaded"},{"name":"mistral","status":"sleeping"}]}"""),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8080/"), routes);

        var detailed = await LlamaCppAdapter.EnumerateModelsDetailedAsync(http, default);
        var loaded = await LlamaCppAdapter.EnumerateModelsAsync(http, default);

        Assert.Equal(2, detailed.Count);
        Assert.Equal("loaded", detailed[0].Status);
        Assert.Equal("sleeping", detailed[1].Status);
        Assert.Single(loaded);
        Assert.Equal("qwen", loaded[0]);
    }

    [Fact]
    public async Task Bug1_LlamaRouter_ShapeC_CurrentRouterStyle_NestedStatusValue()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["models"] = (200, """
            {
              "data": [
                {
                  "id": "model-a",
                  "status": {
                    "value": "loaded"
                  }
                },
                {
                  "id": "model-b",
                  "status": {
                    "value": "unloaded"
                  }
                },
                {
                  "id": "model-c",
                  "status": {
                    "value": "loading"
                  }
                },
                {
                  "id": "model-d",
                  "status": {
                    "value": "failed"
                  }
                }
              ]
            }
            """),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8080/"), routes);

        var detailed = await LlamaCppAdapter.EnumerateModelsDetailedAsync(http, default);
        var loaded = await LlamaCppAdapter.EnumerateModelsAsync(http, default);

        Assert.Equal(4, detailed.Count);
        Assert.Equal("loaded", detailed[0].Status);
        Assert.Equal("unloaded", detailed[1].Status);
        Assert.Equal("loading", detailed[2].Status);
        Assert.Equal("failed", detailed[3].Status);

        // Only model-a is loaded
        var single = Assert.Single(loaded);
        Assert.Equal("model-a", single);
    }

    [Fact]
    public async Task Bug1_LlamaRouter_ShapeD_OpenAiFallback_NoStatusDefaultsToLoaded()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["models"] = (-1, ""),
            ["v1/models"] = (200, """{"object":"list","data":[{"id":"legacy-model"}]}"""),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8080/"), routes);

        var detailed = await LlamaCppAdapter.EnumerateModelsDetailedAsync(http, default);
        var loaded = await LlamaCppAdapter.EnumerateModelsAsync(http, default);

        Assert.Single(detailed);
        Assert.Equal("loaded", detailed[0].Status);
        Assert.Single(loaded);
        Assert.Equal("legacy-model", loaded[0]);
    }

    [Fact]
    public async Task Bug1_LlamaRouter_UnparseableStatusObject_DoesNotDefaultToLoaded()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["models"] = (200, """
            {
              "data": [
                {
                  "id": "empty-status-obj",
                  "status": {}
                },
                {
                  "id": "non-string-status-val",
                  "status": { "value": 123 }
                },
                {
                  "id": "numeric-status",
                  "status": 404
                }
              ]
            }
            """),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8080/"), routes);

        var detailed = await LlamaCppAdapter.EnumerateModelsDetailedAsync(http, default);
        var loaded = await LlamaCppAdapter.EnumerateModelsAsync(http, default);

        Assert.Equal(3, detailed.Count);
        Assert.All(detailed, m => Assert.Equal("unknown", m.Status));
        Assert.Empty(loaded);
    }

    // =========================================================================
    // BUG #4: Stale auto-discovered endpoints reconciliation
    // =========================================================================

    [Fact]
    public void Bug4_DiscoveryReconciliation_RemovesDisappearedServers_AndPrunesCollectors()
    {
        var cfg = new AppConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigurationService(Path.Combine(tempDir, "config.json"));
            using var registry = new BackendRegistry(cfg, cfgSvc);

            var sA = new DiscoveredServer(
                new EndpointRef("win|127.0.0.1:8001", new Uri("http://127.0.0.1:8001"), OriginKind.WindowsHost, null),
                BackendKind.Vllm, "test");
            var sB = new DiscoveredServer(
                new EndpointRef("win|127.0.0.1:8002", new Uri("http://127.0.0.1:8002"), OriginKind.WindowsHost, null),
                BackendKind.LlamaCpp, "test");

            // Scan 1: A and B discovered
            bool c1 = registry.MergeDiscovered([sA, sB]);
            Assert.True(c1);

            var targets1 = registry.GetTargetEntries();
            Assert.Equal(2, targets1.Count);
            Assert.Contains(targets1, t => t.Target.Endpoint.BaseUrl.Port == 8001);
            Assert.Contains(targets1, t => t.Target.Endpoint.BaseUrl.Port == 8002);
            Assert.Equal(2, registry.Collectors.Count);

            // Scan 2: only A discovered (B disappeared)
            bool c2 = registry.MergeDiscovered([sA]);
            Assert.True(c2);

            var targets2 = registry.GetTargetEntries();
            Assert.Single(targets2);
            Assert.Equal(8001, targets2[0].Target.Endpoint.BaseUrl.Port);

            // Collector for B was removed/disposed
            Assert.Equal(1, registry.Collectors.Count);

            // Scan 3: identical scan (A only) -> no change
            bool c3 = registry.MergeDiscovered([sA]);
            Assert.False(c3);

            // Scan 4: B reappears
            bool c4 = registry.MergeDiscovered([sA, sB]);
            Assert.True(c4);
            Assert.Equal(2, registry.GetTargetEntries().Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Bug4_DiscoveryReconciliation_PreservesManualBackendSharingSamePort()
    {
        var cfg = new AppConfiguration();
        cfg.ManualBackends.Add(new ManualEndpointConfig
        {
            Name = "Manual 8001",
            Url = "http://127.0.0.1:8001",
            Type = "Vllm",
        });

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigurationService(Path.Combine(tempDir, "config.json"));
            using var registry = new BackendRegistry(cfg, cfgSvc);

            var sA = new DiscoveredServer(
                new EndpointRef("win|127.0.0.1:8001", new Uri("http://127.0.0.1:8001"), OriginKind.WindowsHost, null),
                BackendKind.Vllm, "test");

            // Auto-discover the same endpoint
            registry.MergeDiscovered([sA]);
            var col = registry.Collectors.GetOrAdd(sA.Endpoint, BackendKind.Vllm);

            // Next scan: auto-discovery for 8001 disappears
            registry.MergeDiscovered([]);

            // Collector must NOT be disposed because manual backend still uses it
            Assert.False(col.IsDisposed);
            Assert.Equal(1, registry.Collectors.Count);

            var manualTargets = registry.GetTargetEntries();
            Assert.Single(manualTargets);
            Assert.Equal("Manual", manualTargets[0].GroupLabel);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // =========================================================================
    // BUG #5: Backend kind change on same host:port
    // =========================================================================

    [Fact]
    public void Bug5_CollectorManager_BackendKindChange_ReplacesCollectorSafely()
    {
        using var mgr = new CollectorManager();
        var endpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null);

        // Time 1: vLLM
        var col1 = mgr.GetOrAdd(endpoint, BackendKind.Vllm);
        Assert.Equal(BackendKind.Vllm, col1.EffectiveKind);
        Assert.False(col1.IsDisposed);

        // Time 2: Same endpoint is now llama.cpp
        var col2 = mgr.GetOrAdd(endpoint, BackendKind.LlamaCpp);
        Assert.NotSame(col1, col2);
        Assert.True(col1.IsDisposed);
        Assert.False(col2.IsDisposed);
        Assert.Equal(BackendKind.LlamaCpp, col2.EffectiveKind);
        Assert.Equal(1, mgr.Count);

        // Time 3: Re-querying with same kind reuses existing collector
        var col3 = mgr.GetOrAdd(endpoint, BackendKind.LlamaCpp);
        Assert.Same(col2, col3);

        // Time 4: Unknown does NOT downgrade a known specific backend
        var col4 = mgr.GetOrAdd(endpoint, BackendKind.Unknown);
        Assert.Same(col2, col4);
        Assert.Equal(BackendKind.LlamaCpp, col4.EffectiveKind);
    }

    [Fact]
    public void Bug5_CollectorManager_AuthTokenChange_DoesNotRecreateCollector()
    {
        using var mgr = new CollectorManager();
        var ep1 = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null, "token-1");
        var col1 = mgr.GetOrAdd(ep1, BackendKind.Vllm);

        var ep2 = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null, "token-2");
        var col2 = mgr.GetOrAdd(ep2, BackendKind.Vllm);

        Assert.Same(col1, col2);
        Assert.False(col1.IsDisposed);
        Assert.Equal("token-2", col1.Endpoint.AuthToken);
    }

    // =========================================================================
    // BUG #6: SnapshotUpdated subscriber exception isolation
    // =========================================================================

    [Fact]
    public void Bug6_SubscriberException_DoesNotCrashPublishOrBreakOtherSubscribers()
    {
        var endpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null);
        using var collector = new BackendCollector(endpoint, BackendKind.GenericOpenAi);

        var receivedSnapshots = new List<MetricSnapshot>();

        // Subscriber 1 throws every time
        collector.SnapshotUpdated += _ => throw new InvalidOperationException("UI render crashed");
        // Subscriber 2 is healthy
        collector.SnapshotUpdated += s => receivedSnapshots.Add(s);

        var snap = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Online,
            Kind = BackendKind.GenericOpenAi,
        };

        // Publish must not throw
        collector.Publish(snap);

        Assert.Same(snap, collector.Latest);
        Assert.Single(receivedSnapshots);
        Assert.Same(snap, receivedSnapshots[0]);
    }

    // =========================================================================
    // BUG #7: Healthy idle NInfer telemetry is not declared stale
    // =========================================================================

    [Fact]
    public async Task Bug7_NInfer_HealthyIdle_RemainsOnlineWithZeroRates()
    {
        var http = new FakeHttp(new Uri("http://127.0.0.1:8123/"), new Dictionary<string, (int, string)>
        {
            ["health"] = (200, """{"status":"ok"}"""),
            ["v1/models"] = (200, """{"object":"list","data":[{"id":"qwen"}]}"""),
        });
        var endpoint = new EndpointRef("win|127.0.0.1:8123", http.BaseUrl, OriginKind.WindowsHost, null);
        using var reader = new NInferJsonlTelemetryReader();
        using var adapter = new NInferAdapter(endpoint, reader, ownsReader: false);

        string tempFile = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;
            long t0 = 1000;
            adapter.Clock = () => t0;

            // 1. server_start + request_start + throughput + request_done
            string lines = """
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":1000,"server_instance_id":"inst-1","server":{"public_model_id":"qwen-32b"}}
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_start","timestamp_unix_ms":2000,"server_instance_id":"inst-1","request":{"request_id":1}}
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":3000,"server_instance_id":"inst-1","interval_seconds":2.0,"tokens":{"computed_prefill":1000,"committed_decode":200},"scheduler":{"running":1,"waiting":0}}
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_done","timestamp_unix_ms":4000,"server_instance_id":"inst-1","request":{"request_id":1},"timings_seconds":{"ttft":0.035}}
            """;
            File.WriteAllText(tempFile, lines + "\n");

            var s1 = await adapter.CollectAsync(http, default);
            Assert.Equal(ConnectionState.Online, s1.State);
            Assert.Equal(0, s1.Running.Value);
            Assert.Equal(0.0, s1.PrefillTokPerSec.Value);
            Assert.Equal(0.0, s1.GenerationTokPerSec.Value);
            Assert.Equal(1000, s1.PrefilledTokensTotal.Value);
            Assert.Equal(200, s1.GeneratedTokensTotal.Value);

            // 2. Advance clock by 60 seconds (well past the 30s stale threshold) with NO new JSONL events
            long t1 = t0 + (long)(60 * System.Diagnostics.Stopwatch.Frequency);
            adapter.Clock = () => t1;

            var s2 = await adapter.CollectAsync(http, default);

            // Must remain Online, NOT degrade to Limited!
            Assert.Equal(ConnectionState.Online, s2.State);
            Assert.NotEqual(ConnectionState.Limited, s2.State);
            Assert.Equal(0, s2.Running.Value);
            Assert.Equal(0.0, s2.PrefillTokPerSec.Value);
            Assert.Equal(0.0, s2.GenerationTokPerSec.Value);
            Assert.Equal(1000, s2.PrefilledTokensTotal.Value);
            Assert.Equal(200, s2.GeneratedTokensTotal.Value);
            Assert.True(s2.RecentTtftMs.HasValue);
            Assert.Equal(35.0, s2.RecentTtftMs.Value, 1);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task Bug7_NInfer_ActiveRequest_TelemetrySilence_TransitionsToLimited()
    {
        var http = new FakeHttp(new Uri("http://127.0.0.1:8123/"), new Dictionary<string, (int, string)>
        {
            ["health"] = (200, """{"status":"ok"}"""),
            ["v1/models"] = (200, """{"object":"list","data":[{"id":"qwen"}]}"""),
        });
        var endpoint = new EndpointRef("win|127.0.0.1:8123", http.BaseUrl, OriginKind.WindowsHost, null);
        using var reader = new NInferJsonlTelemetryReader();
        using var adapter = new NInferAdapter(endpoint, reader, ownsReader: false);

        string tempFile = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;
            long t0 = 1000;
            adapter.Clock = () => t0;

            // request_start without request_done (request is active)
            string lines = """
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":1000,"server_instance_id":"inst-1","server":{"public_model_id":"qwen"}}
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"request_start","timestamp_unix_ms":2000,"server_instance_id":"inst-1","request":{"request_id":1}}
            """;
            File.WriteAllText(tempFile, lines + "\n");

            var s1 = await adapter.CollectAsync(http, default);
            Assert.Equal(ConnectionState.Online, s1.State);
            Assert.Equal(1, reader.ActiveRequestCount);

            // Advance clock by 60 seconds with active request still pending but telemetry silent
            long t1 = t0 + (long)(60 * System.Diagnostics.Stopwatch.Frequency);
            adapter.Clock = () => t1;

            var s2 = await adapter.CollectAsync(http, default);
            Assert.Equal(ConnectionState.Limited, s2.State);
            Assert.Equal("NInfer telemetry log is stale", s2.Info["Telemetry"]);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // =========================================================================
    // BUG #8: NInfer JSONL file replacement/rotation detection
    // =========================================================================

    [Fact]
    public void Bug8_NInfer_FileReplacement_SameSize_DetectedAndReReadFromBeginning()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        string replaceSource = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;

            string log1 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":1000,"server_instance_id":"inst-1","server":{"public_model_id":"model-AAA"}}""" + "\n";
            File.WriteAllText(tempFile, log1);

            Assert.True(reader.Poll(100));
            Assert.Equal("inst-1", reader.ServerInstanceId);
            Assert.Equal("model-A", reader.PublicModelId?.Substring(0, 7));

            // Create a replacement file with the exact same character count
            string log2 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":2000,"server_instance_id":"inst-2","server":{"public_model_id":"model-BBB"}}""" + "\n";
            Assert.Equal(log1.Length, log2.Length);

            File.WriteAllText(replaceSource, log2);
            File.Move(replaceSource, tempFile, overwrite: true);

            // Poll should detect atomic file replacement even at same length!
            Assert.True(reader.Poll(200));
            Assert.Equal("inst-2", reader.ServerInstanceId);
            Assert.Equal("model-BBB", reader.PublicModelId);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(replaceSource); } catch { }
        }
    }

    [Fact]
    public void Bug8_NInfer_FileReplacement_Larger_DetectedAndReReadFromBeginning()
    {
        using var reader = new NInferJsonlTelemetryReader();
        string tempFile = Path.GetTempFileName();
        string replaceSource = Path.GetTempFileName();
        try
        {
            reader.FilePath = tempFile;

            string log1 = """{"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":1000,"server_instance_id":"inst-1","server":{"public_model_id":"small"}}""" + "\n";
            File.WriteAllText(tempFile, log1);

            Assert.True(reader.Poll(100));
            Assert.Equal("inst-1", reader.ServerInstanceId);

            // Create a strictly larger replacement file
            string log2 = """
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"server_start","timestamp_unix_ms":2000,"server_instance_id":"inst-2","server":{"public_model_id":"much-larger-model"}}
            {"artifact_type":"ninfer_serve_request_log","schema_version":10,"event":"throughput","timestamp_unix_ms":3000,"server_instance_id":"inst-2","interval_seconds":2.0,"tokens":{"computed_prefill":888,"committed_decode":999},"scheduler":{"running":0,"waiting":0}}
            """ + "\n";
            Assert.True(log2.Length > log1.Length);

            File.WriteAllText(replaceSource, log2);
            File.Move(replaceSource, tempFile, overwrite: true);

            Assert.True(reader.Poll(200));
            Assert.Equal("inst-2", reader.ServerInstanceId);
            Assert.Equal("much-larger-model", reader.PublicModelId);

            var builder = new MetricSnapshotBuilder();
            reader.PopulateSnapshot(builder);
            Assert.Equal(888, builder.PrefilledTokensTotal.Value);
            Assert.Equal(999, builder.GeneratedTokensTotal.Value);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(replaceSource); } catch { }
        }
    }

    // =========================================================================
    // BUG #10: Prometheus NaN / Inf sample handling
    // =========================================================================

    [Fact]
    public async Task Bug10_Vllm_RunningNaN_HandledAsUnavailable()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, """
            vllm:num_requests_running NaN
            vllm:num_requests_waiting 0
            vllm:prompt_tokens_total 100
            vllm:generation_tokens_total 50
            """),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8000/"), routes);
        var adapter = new VllmAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.False(snap.Running.HasValue);
        Assert.True(snap.Queued.HasValue);
        Assert.Equal(0, snap.Queued.Value);
    }

    [Fact]
    public async Task Bug10_Vllm_GenCounterInf_DoesNotCorruptBaseline()
    {
        var adapter = new VllmAdapter();
        long t = 1000;
        adapter.Clock = () => t;

        // Sample 1: finite counter (establishes baseline)
        var http1 = new FakeHttp(new Uri("http://127.0.0.1:8000/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "vllm:generation_tokens_total 1000\nvllm:prompt_tokens_total 500\n"),
        });
        var s1 = await adapter.CollectAsync(http1, default);
        Assert.False(s1.GenerationTokPerSec.HasValue); // first sample establishes baseline

        // Sample 2: +Inf counter (must NOT corrupt baseline)
        t += System.Diagnostics.Stopwatch.Frequency;
        var http2 = new FakeHttp(new Uri("http://127.0.0.1:8000/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "vllm:generation_tokens_total +Inf\nvllm:prompt_tokens_total 500\n"),
        });
        var s2 = await adapter.CollectAsync(http2, default);
        Assert.False(s2.GenerationTokPerSec.HasValue);

        // Sample 3: next finite counter (delta computed against 1000)
        t += System.Diagnostics.Stopwatch.Frequency;
        var http3 = new FakeHttp(new Uri("http://127.0.0.1:8000/"), new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "vllm:generation_tokens_total 1200\nvllm:prompt_tokens_total 500\n"),
        });
        var s3 = await adapter.CollectAsync(http3, default);
        Assert.True(s3.GenerationTokPerSec.HasValue);
        Assert.True(s3.GenerationTokPerSec.Value >= 0);
    }

    [Fact]
    public async Task Bug10_Vllm_KvMetricNaN_Ignored()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "vllm:kv_cache_usage_perc NaN\n"),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8000/"), routes);
        var adapter = new VllmAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.False(snap.KvCacheUsage.HasValue);
    }

    [Fact]
    public async Task Bug10_Vllm_FiniteAndNaN_AggregatesFiniteOnly()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, """
            vllm:num_requests_waiting{model="m1"} 4
            vllm:num_requests_waiting{model="m2"} NaN
            """),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8000/"), routes);
        var adapter = new VllmAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.True(snap.Queued.HasValue);
        Assert.Equal(4, snap.Queued.Value);
    }

    [Fact]
    public void Bug10_RateCalculator_Update_GuardsNonFinite()
    {
        var rc = new RateCalculator();
        var r1 = rc.Update(100, 1000);
        Assert.False(r1.HasValue);

        // Feed NaN
        var r2 = rc.Update(double.NaN, 2000);
        Assert.False(r2.HasValue);

        // Feed +Inf
        var r3 = rc.Update(double.PositiveInfinity, 3000);
        Assert.False(r3.HasValue);

        // Next finite sample computes rate using valid baseline 100 at tick 1000
        var r4 = rc.Update(200, 1000 + RateCalculator.StopwatchTicksPerSecond);
        Assert.True(r4.HasValue);
        Assert.InRange(r4.Value, 99.9, 100.1);
    }

    [Fact]
    public void Bug10_RollingTtft_NaNInf_DoesNotCorruptAverage()
    {
        var ttft = new RollingTtft(10);
        ttft.Observe(1, 0.050, 100);

        // Feed NaN
        ttft.Observe(2, double.NaN, 200);
        // Feed +Inf
        ttft.Observe(2, double.PositiveInfinity, 250);

        // Feed next valid observation
        ttft.Observe(2, 0.100, 300);

        var avg = ttft.AverageSeconds();
        Assert.NotNull(avg);
        Assert.InRange(avg.Value, 0.049, 0.051); // 0.050 and 0.050
    }

    [Fact]
    public void Bug10_RateHistoryAndChart_NonFiniteRatesFiltered()
    {
        var history = new RateHistory();
        var snap = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Online,
            Kind = BackendKind.GenericOpenAi,
            PrefillTokPerSec = MetricValue<double>.Approx(double.NaN, MetricSource.Derived),
            GenerationTokPerSec = MetricValue<double>.Approx(double.PositiveInfinity, MetricSource.Derived),
        };

        history.Record(snap);

        var prefillPoints = history.PrefillSnapshot(DateTimeOffset.Now);
        var genPoints = history.GenerateSnapshot(DateTimeOffset.Now);

        Assert.Single(prefillPoints);
        Assert.Null(prefillPoints[0].Value);

        Assert.Single(genPoints);
        Assert.Null(genPoints[0].Value);

        // ActivityChart scale helper must not produce NaN or Infinity
        double maxNaN = ActivityChart.NiceScaleMaximum(double.NaN);
        double maxInf = ActivityChart.NiceScaleMaximum(double.PositiveInfinity);
        Assert.Equal(1.0, maxNaN);
        Assert.Equal(1.0, maxInf);
    }
}
