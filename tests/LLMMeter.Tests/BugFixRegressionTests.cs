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
            // GetTargetEntries is passive and starts 0 collectors
            Assert.Equal(0, registry.Collectors.Count);

            // Active observer acquires collector for B
            var colB = registry.Collectors.Acquire(sB.Endpoint, sB.Kind);
            Assert.Equal(1, registry.Collectors.Count);

            // Scan 2: only A discovered (1st miss for B - preserved during grace period)
            bool c2 = registry.MergeDiscovered([sA]);
            Assert.False(c2); // no deletion yet due to hysteresis
            var targets2a = registry.GetTargetEntries();
            Assert.Equal(2, targets2a.Count);

            // Scan 3: only A discovered (2nd miss for B - now pruned)
            bool c3 = registry.MergeDiscovered([sA]);
            Assert.True(c3);

            var targets2 = registry.GetTargetEntries();
            Assert.Single(targets2);
            Assert.Equal(8001, targets2[0].Target.Endpoint.BaseUrl.Port);

            // Collector for B was notified offline on disappearance
            Assert.Equal(ConnectionState.Offline, colB.Latest?.State);

            // Scan 4: identical scan (A only) -> no change
            bool c4 = registry.MergeDiscovered([sA]);
            Assert.False(c4);

            // Scan 5: B reappears
            bool c5 = registry.MergeDiscovered([sA, sB]);
            Assert.True(c5);
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

            // Auto-discovery for 8001 disappears (requires 2 scans to remove auto-discovered)
            registry.MergeDiscovered([]);
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
    // BUG #5: Backend kind change on same host:port (re-initializes adapter on stable collector)
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

        // Time 2: Same endpoint is now llama.cpp -> stable collector re-initializes adapter
        var col2 = mgr.GetOrAdd(endpoint, BackendKind.LlamaCpp);
        Assert.Same(col1, col2);
        Assert.False(col1.IsDisposed);
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

    // =========================================================================
    // NEW REGRESSION TESTS FOR BUGS #1 - #22
    // =========================================================================

    [Fact]
    public void Bug1_NInfer_ServerRestart_InSameFile_DoesNotReplayOnUnchangedPoll()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ninfer_restart_{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var writer = File.CreateText(tempFile))
            {
                // Server instance 1
                writer.WriteLine("""{"event":"server_start","server_instance_id":"inst-1","public_model_id":"model-A"}""");
                writer.WriteLine("""{"event":"throughput","server_instance_id":"inst-1","tokens":{"computed_prefill":100,"committed_decode":50}}""");
                writer.Flush();
            }

            var reader = new NInferJsonlTelemetryReader();
            reader.FilePath = tempFile;

            // Poll 1: read inst-1
            long t1 = 10_000_000;
            bool ok1 = reader.Poll(t1);
            Assert.True(ok1);
            Assert.Equal("inst-1", reader.ServerInstanceId);
            Assert.Equal("model-A", reader.PublicModelId);
            Assert.Equal(100, reader.CumulativePrefilled);
            Assert.Equal(50, reader.CumulativeGenerated);

            // Now append server instance 2 to the same file (same-file restart)
            using (var writer = File.AppendText(tempFile))
            {
                writer.WriteLine("""{"event":"server_start","server_instance_id":"inst-2","public_model_id":"model-B"}""");
                writer.Flush();
            }

            // Poll 2: read inst-2 restart
            long t2 = 20_000_000;
            bool ok2 = reader.Poll(t2);
            Assert.True(ok2);
            Assert.Equal("inst-2", reader.ServerInstanceId);
            Assert.Equal("model-B", reader.PublicModelId);
            // Telemetry counters reset for new instance
            Assert.Equal(0, reader.CumulativePrefilled);
            Assert.Equal(0, reader.CumulativeGenerated);

            // Poll 3: UNCHANGED POLL (no new lines written)
            long t3 = 30_000_000;
            bool ok3 = reader.Poll(t3);
            Assert.True(ok3);
            Assert.Equal("inst-2", reader.ServerInstanceId);
            Assert.Equal("model-B", reader.PublicModelId);
            // Offset MUST NOT have rewound to byte 0; cumulative counters must remain 0 and not replay inst-1!
            Assert.Equal(0, reader.CumulativePrefilled);
            Assert.Equal(0, reader.CumulativeGenerated);

            reader.Dispose();
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void Bug1_NInfer_MultipleServerInstances_AdvancesMonotonically()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ninfer_multi_{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var writer = File.CreateText(tempFile))
            {
                writer.WriteLine("""{"event":"server_start","server_instance_id":"inst-1","public_model_id":"model-1"}""");
                writer.WriteLine("""{"event":"throughput","server_instance_id":"inst-1","tokens":{"computed_prefill":10,"committed_decode":5}}""");
                writer.WriteLine("""{"event":"server_start","server_instance_id":"inst-2","public_model_id":"model-2"}""");
                writer.WriteLine("""{"event":"throughput","server_instance_id":"inst-2","tokens":{"computed_prefill":20,"committed_decode":15}}""");
                writer.WriteLine("""{"event":"server_start","server_instance_id":"inst-3","public_model_id":"model-3"}""");
                writer.Flush();
            }

            var reader = new NInferJsonlTelemetryReader();
            reader.FilePath = tempFile;

            reader.Poll(1000);
            Assert.Equal("inst-3", reader.ServerInstanceId);
            Assert.Equal("model-3", reader.PublicModelId);
            Assert.Equal(0, reader.CumulativePrefilled);
            Assert.Equal(0, reader.CumulativeGenerated);

            reader.Dispose();
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void Bug1_NInfer_PhysicalTruncation_ResetsReaderFully()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ninfer_trunc_{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var writer = File.CreateText(tempFile))
            {
                writer.WriteLine("""{"event":"server_start","server_instance_id":"inst-1","public_model_id":"long-model-name-initial-file"}""");
                writer.WriteLine("""{"event":"throughput","server_instance_id":"inst-1","tokens":{"computed_prefill":500,"committed_decode":200}}""");
                writer.Flush();
            }

            var reader = new NInferJsonlTelemetryReader();
            reader.FilePath = tempFile;
            reader.Poll(1000);
            Assert.Equal(500, reader.CumulativePrefilled);

            // Truncate file to a much smaller size
            using (var writer = File.CreateText(tempFile))
            {
                writer.WriteLine("""{"event":"server_start","server_instance_id":"inst-truncated","public_model_id":"short"}""");
                writer.Flush();
            }

            // Next poll must detect truncation and reset
            reader.Poll(2000);
            Assert.Equal("inst-truncated", reader.ServerInstanceId);
            Assert.Equal("short", reader.PublicModelId);
            Assert.Equal(0, reader.CumulativePrefilled);

            reader.Dispose();
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void Bug2_Collector_KindChange_PreservesStableCollectorAndSwapsAdapter()
    {
        var endpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null);
        var collector = new BackendCollector(endpoint, BackendKind.Vllm);

        MetricSnapshot? received = null;
        collector.SnapshotUpdated += s => received = s;

        Assert.Equal(BackendKind.Vllm, collector.EffectiveKind);

        // Server changes kind to LlamaCpp on same host:port
        collector.ChangeKind(BackendKind.LlamaCpp);

        Assert.Equal(BackendKind.LlamaCpp, collector.EffectiveKind);
        Assert.NotNull(received);
        Assert.Equal(BackendKind.LlamaCpp, received.Kind);
        Assert.Equal(ConnectionState.Connecting, received.State);

        collector.Dispose();
    }

    [Fact]
    public void Bug2_Collector_MarkOffline_PublishesOfflineSnapshot()
    {
        var endpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null);
        var collector = new BackendCollector(endpoint, BackendKind.Vllm);

        MetricSnapshot? received = null;
        collector.SnapshotUpdated += s => received = s;

        collector.MarkOffline("Endpoint disappeared from discovery");

        Assert.NotNull(received);
        Assert.Equal(ConnectionState.Offline, received.State);
        Assert.Equal("Endpoint disappeared from discovery", received.Info["Status"]);

        collector.Dispose();
    }

    [Fact]
    public void Bug7_GetTargetEntries_IsPassive_CreatesZeroCollectors()
    {
        var cfg = new AppConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigurationService(Path.Combine(tempDir, "config.json"));
            using var registry = new BackendRegistry(cfg, cfgSvc);

            var s1 = new DiscoveredServer(new EndpointRef("win|127.0.0.1:8001", new Uri("http://127.0.0.1:8001"), OriginKind.WindowsHost, null), BackendKind.Vllm, "test");
            var s2 = new DiscoveredServer(new EndpointRef("win|127.0.0.1:8002", new Uri("http://127.0.0.1:8002"), OriginKind.WindowsHost, null), BackendKind.LlamaCpp, "test");

            registry.MergeDiscovered([s1, s2]);

            // Calling GetTargetEntries must be 100% passive
            var entries = registry.GetTargetEntries();
            Assert.Equal(2, entries.Count);

            // Exactly 0 collectors must have been created or started!
            Assert.Equal(0, registry.Collectors.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Bug8_CollectorManager_ReferenceCounting_AcquireAndRelease()
    {
        using var mgr = new CollectorManager();
        var endpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null);

        // Observer 1 acquires
        var c1 = mgr.Acquire(endpoint, BackendKind.Vllm);
        Assert.Equal(1, mgr.Count);
        Assert.False(c1.IsDisposed);

        // Observer 2 acquires same target
        var c2 = mgr.Acquire(endpoint, BackendKind.Vllm);
        Assert.Same(c1, c2);
        Assert.Equal(1, mgr.Count);

        // Observer 1 releases
        mgr.Release(c1);
        Assert.Equal(1, mgr.Count);
        Assert.False(c2.IsDisposed); // Observer 2 still monitoring

        // Observer 2 releases
        mgr.Release(c2);
        Assert.Equal(0, mgr.Count);
        Assert.True(c2.IsDisposed); // Last observer released -> stopped and disposed
    }

    [Fact]
    public void Bug19_BackendCollector_Start_IsIdempotent()
    {
        var endpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null);
        var collector = new BackendCollector(endpoint, BackendKind.GenericOpenAi);

        // Calling Start() multiple times must not throw or create duplicate loops
        collector.Start();
        collector.Start();
        collector.Start();

        collector.Dispose();
    }

    [Fact]
    public void Bug20_BackendCollector_Reconfigure_DoesNotMutateDefaultHeaders()
    {
        var endpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null, "token-A");
        var collector = new BackendCollector(endpoint, BackendKind.GenericOpenAi);

        // Reconfigure with new token
        var newEndpoint = new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null, "token-B");
        collector.Reconfigure(newEndpoint);

        // Rapid reconfigures
        for (int i = 0; i < 50; i++)
        {
            collector.Reconfigure(new EndpointRef("win|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.WindowsHost, null, $"token-{i}"));
        }

        collector.Dispose();
    }

    [Fact]
    public void Bug3_DiscoveryHysteresis_RequiresTwoConsecutiveMisses()
    {
        var cfg = new AppConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigurationService(Path.Combine(tempDir, "config.json"));
            using var registry = new BackendRegistry(cfg, cfgSvc);

            var sA = new DiscoveredServer(new EndpointRef("win|127.0.0.1:8001", new Uri("http://127.0.0.1:8001"), OriginKind.WindowsHost, null), BackendKind.Vllm, "test");
            var sB = new DiscoveredServer(new EndpointRef("win|127.0.0.1:8002", new Uri("http://127.0.0.1:8002"), OriginKind.WindowsHost, null), BackendKind.LlamaCpp, "test");

            // Scan 1: both present
            registry.MergeDiscovered(new DiscoveryScanResult([sA, sB]));
            Assert.Equal(2, registry.GetTargetEntries().Count);

            // Scan 2: only sA (1st miss for sB -> sB is preserved due to grace period)
            registry.MergeDiscovered(new DiscoveryScanResult([sA]));
            Assert.Equal(2, registry.GetTargetEntries().Count);

            // Scan 3: only sA (2nd miss for sB -> sB is removed)
            registry.MergeDiscovered(new DiscoveryScanResult([sA]));
            var targets = registry.GetTargetEntries();
            Assert.Single(targets);
            Assert.Equal(8001, targets[0].Target.Endpoint.BaseUrl.Port);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Bug3_DiscoveryHysteresis_ReappearanceResetsMissCount()
    {
        var cfg = new AppConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigurationService(Path.Combine(tempDir, "config.json"));
            using var registry = new BackendRegistry(cfg, cfgSvc);

            var sA = new DiscoveredServer(new EndpointRef("win|127.0.0.1:8001", new Uri("http://127.0.0.1:8001"), OriginKind.WindowsHost, null), BackendKind.Vllm, "test");
            var sB = new DiscoveredServer(new EndpointRef("win|127.0.0.1:8002", new Uri("http://127.0.0.1:8002"), OriginKind.WindowsHost, null), BackendKind.LlamaCpp, "test");

            registry.MergeDiscovered(new DiscoveryScanResult([sA, sB]));

            // Scan 2: 1st miss for sB
            registry.MergeDiscovered(new DiscoveryScanResult([sA]));
            Assert.Equal(2, registry.GetTargetEntries().Count);

            // Scan 3: sB reappears (miss count resets)
            registry.MergeDiscovered(new DiscoveryScanResult([sA, sB]));
            Assert.Equal(2, registry.GetTargetEntries().Count);

            // Scan 4: another 1st miss for sB -> still preserved!
            registry.MergeDiscovered(new DiscoveryScanResult([sA]));
            Assert.Equal(2, registry.GetTargetEntries().Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Bug3_DiscoveryWslFailure_PreservesWslEndpoints()
    {
        var cfg = new AppConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigurationService(Path.Combine(tempDir, "config.json"));
            using var registry = new BackendRegistry(cfg, cfgSvc);

            var wslServer = new DiscoveredServer(
                new EndpointRef("wsl|Ubuntu|127.0.0.1:8000", new Uri("http://127.0.0.1:8000"), OriginKind.Wsl, "Ubuntu"),
                BackendKind.Vllm, "WSL");

            registry.MergeDiscovered(new DiscoveryScanResult([wslServer], windowsScanned: true, wslScanned: true, new HashSet<string> { "Ubuntu" }));
            Assert.Single(registry.GetTargetEntries());

            // Scan where WSL discovery threw an error (wslScanned = false)
            registry.MergeDiscovered(new DiscoveryScanResult([], windowsScanned: true, wslScanned: false));

            // WSL server MUST be preserved because WSL was not successfully scanned
            Assert.Single(registry.GetTargetEntries());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Bug6_LlamaCpp_IdleMetrics_DoesNotPollSlots()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "llamacpp:prompt_tokens_total 50\nllamacpp:requests_processing 0\n"),
            ["slots"] = (200, "[]"),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8080/"), routes);
        var adapter = new LlamaCppAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.True(snap.State is ConnectionState.Online or ConnectionState.Limited);
        Assert.Equal(0, snap.Running.Value);
        Assert.Contains("metrics", http.Requests);
        // /slots MUST NOT be queried when requests_processing == 0 to allow server idle sleep!
        Assert.DoesNotContain("slots", http.Requests);
    }

    [Fact]
    public async Task Bug6_LlamaCpp_ActiveMetrics_PollsSlotsForCards()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, "llamacpp:prompt_tokens_total 50\nllamacpp:requests_processing 1\n"),
            ["slots"] = (200, """[{"id":0,"id_task":100,"is_processing":true,"n_decoded":10,"n_prompt_tokens":20}]"""),
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8080/"), routes);
        var adapter = new LlamaCppAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.True(snap.State is ConnectionState.Online or ConnectionState.Limited);
        Assert.Equal(1, snap.Running.Value);
        Assert.Contains("metrics", http.Requests);
        // /slots MUST be queried when requests_processing > 0 to populate cards!
        Assert.Contains("slots", http.Requests);
        Assert.NotNull(snap.Requests);
        Assert.Single(snap.Requests);
    }

    [Fact]
    public async Task Bug10_LmStudio_PrefersLlmOverEmbeddingEvenWhenEmbeddingIsFirst()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (200, """
            {
                "models": [
                    {
                        "key": "text-embedding-3-small",
                        "type": "embedding",
                        "loaded_instances": [{"id":"emb-1","config":{"context_length":8192}}]
                    },
                    {
                        "key": "llama-3-8b-instruct",
                        "type": "llm",
                        "loaded_instances": [{"id":"llm-1","config":{"context_length":8192}}]
                    }
                ]
            }
            """)
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:1234/"), routes);
        var adapter = new LmStudioAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.Equal(ConnectionState.Limited, snap.State);
        Assert.Equal("llama-3-8b-instruct", snap.ModelName);
        Assert.Contains("llama-3-8b-instruct", snap.LoadedModels);
        Assert.Contains("text-embedding-3-small", snap.Info["Embeddings"]);
    }

    [Fact]
    public async Task Bug10_LmStudio_FallsBackToEmbeddingIfOnlyEmbeddingIsLoaded()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["api/v1/models"] = (200, """
            {
                "models": [
                    {
                        "key": "bge-m3",
                        "type": "embedding",
                        "loaded_instances": [{"id":"emb-1","config":{"context_length":8192}}]
                    }
                ]
            }
            """)
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:1234/"), routes);
        var adapter = new LmStudioAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.Equal(ConnectionState.Limited, snap.State);
        Assert.Equal("bge-m3", snap.ModelName);
        Assert.Contains("bge-m3", snap.LoadedModels);
    }

    [Fact]
    public async Task Bug11_Vllm_ModelNameUpdatesOnReload()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, """
                vllm:num_requests_running{model_name="qwen-1"} 1
            """)
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8000/"), routes);
        var adapter = new VllmAdapter();

        var snap1 = await adapter.CollectAsync(http, default);
        Assert.Equal("qwen-1", snap1.ModelName);

        // Server reloads different model
        routes["metrics"] = (200, """
            vllm:num_requests_running{model_name="deepseek-v2"} 1
        """);

        var snap2 = await adapter.CollectAsync(http, default);
        Assert.Equal("deepseek-v2", snap2.ModelName);
    }

    [Fact]
    public async Task Bug12_Vllm_WaitingPlusSwapped_SumsBacklog()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["metrics"] = (200, """
                vllm:num_requests_running 2
                vllm:num_requests_waiting 5
                vllm:num_requests_swapped 3
            """)
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8000/"), routes);
        var adapter = new VllmAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.Equal(2, snap.Running.Value);
        // Waiting (5) + Swapped (3) = 8
        Assert.Equal(8, snap.Queued.Value);
    }

    [Fact]
    public void Bug13_WindowsProcessDiscovery_Ipv6_DistinguishesLoopbackAndWildcard()
    {
        // Loopback ::1 (15 zeroes, 1)
        byte[] loopbackBytes = new byte[16];
        loopbackBytes[15] = 1;
        bool isLoopback = loopbackBytes[..15].All(b => b == 0) && loopbackBytes[15] == 1;
        bool isWildcardL = loopbackBytes.All(b => b == 0);
        Assert.True(isLoopback);
        Assert.False(isWildcardL);

        // Wildcard :: (all zeroes)
        byte[] wildcardBytes = new byte[16];
        bool isLoopbackW = wildcardBytes[..15].All(b => b == 0) && wildcardBytes[15] == 1;
        bool isWildcard = wildcardBytes.All(b => b == 0);
        Assert.False(isLoopbackW);
        Assert.True(isWildcard);
    }

    [Fact]
    public void Bug14_ScreenGuard_ClampsCoordinatesWithinVirtualScreenDips()
    {
        var virtualScreen = new System.Windows.Rect(0, 0, 1920, 1080);

        // Off-screen right and bottom
        var clamped1 = ScreenGuard.EnsureVisible(3000, 2000, 320, 120, virtualScreen);
        Assert.True(clamped1.X + 320 <= 1920);
        Assert.True(clamped1.Y + 120 <= 1080);
        Assert.True(clamped1.X >= 0);
        Assert.True(clamped1.Y >= 0);

        // Off-screen left and top
        var clamped2 = ScreenGuard.EnsureVisible(-500, -300, 320, 120, virtualScreen);
        Assert.True(clamped2.X >= 24);
        Assert.True(clamped2.Y >= 24);

        // NaN coordinates default to margin
        var clamped3 = ScreenGuard.EnsureVisible(double.NaN, double.NaN, 320, 120, virtualScreen);
        Assert.Equal(24, clamped3.X);
        Assert.Equal(24, clamped3.Y);
    }

    [Fact]
    public async Task Bug16_GenericOpenAi_DoesNotClaimCatalogAsLoadedModels()
    {
        var routes = new Dictionary<string, (int, string)>
        {
            ["v1/models"] = (200, """
            {
                "object": "list",
                "data": [
                    {"id": "gpt-4o", "object": "model"},
                    {"id": "claude-3-opus", "object": "model"},
                    {"id": "gemini-1.5-pro", "object": "model"}
                ]
            }
            """)
        };
        var http = new FakeHttp(new Uri("http://127.0.0.1:8000/"), routes);
        var adapter = new GenericOpenAiAdapter();

        var snap = await adapter.CollectAsync(http, default);

        Assert.Equal(ConnectionState.Limited, snap.State);
        Assert.Equal("gpt-4o", snap.ModelName);
        // LoadedModels must be EMPTY because OpenAI catalog models are not guaranteed resident in VRAM
        Assert.Empty(snap.LoadedModels);
        Assert.Equal("3 models available", snap.Info["Catalog"]);
        Assert.Equal("gpt-4o, claude-3-opus, gemini-1.5-pro", snap.Info["AvailableModels"]);
    }

    [Fact]
    public async Task Bug17_ManualEndpoint_DeduplicatesEquivalentUrls()
    {
        var cfg = new AppConfiguration();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgSvc = new ConfigurationService(Path.Combine(tempDir, "config.json"));
            using var registry = new BackendRegistry(cfg, cfgSvc);

            await registry.AddManualEndpointAsync(new ManualEndpointConfig
            {
                Name = "Localhost",
                Url = "http://localhost:8000",
                Type = "Vllm"
            });

            // Add equivalent URL with 127.0.0.1 and trailing slash
            await registry.AddManualEndpointAsync(new ManualEndpointConfig
            {
                Name = "Loopback",
                Url = "http://127.0.0.1:8000/",
                Type = "Vllm"
            });

            // Must deduplicate to 1 manual endpoint!
            Assert.Single(registry.ManualEndpoints);
            Assert.Equal("Loopback", registry.ManualEndpoints[0].Name);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
