using System.IO;
using System.Text;
using System.Text.Json;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// Incremental tailing reader for native NInfer JSONL request logs (--request-log-jsonl).
/// Reads only newly appended bytes on each poll without re-reading the entire file.
/// Handles file truncation, replacement, partial line buffering, and server restarts.
/// </summary>
public sealed class NInferJsonlTelemetryReader : IDisposable
{
    private readonly object _lock = new();
    private string? _filePath;
    private long _offset;
    private string _remainder = string.Empty;
    private string? _serverInstanceId;

    // Parsed state
    private int? _running;
    private int? _queued;
    private double? _prefillRate;
    private double? _decodeRate;
    private long _cumulativePrefilled;
    private long _cumulativeGenerated;
    private bool _hasThroughputBaseline;
    private long _lastEventTicks;
    private double _lastIntervalSeconds;

    // Rolling TTFT (last 10 completed requests)
    private readonly List<double> _ttftSamples = new(10);

    // Active requests (id -> state)
    private sealed class ActiveRequestState
    {
        public long Id { get; init; }
        public DateTimeOffset StartTime { get; init; }
    }
    private readonly Dictionary<long, ActiveRequestState> _activeRequests = new();

    // Server metadata resolved from server_start
    private readonly Dictionary<string, string> _serverInfo = new(StringComparer.OrdinalIgnoreCase);
    private string? _publicModelId;

    // Speculative acceptance tracking
    private long _specDraftedTokens;
    private long _specAcceptedTokens;
    private string? _specBackend;
    private int _specDraftWindow;

    public NInferJsonlTelemetryReader(string? filePath = null)
    {
        _filePath = filePath;
    }

    public string? FilePath
    {
        get { lock (_lock) return _filePath; }
        set
        {
            lock (_lock)
            {
                if (string.Equals(_filePath, value, StringComparison.OrdinalIgnoreCase)) return;
                _filePath = value;
                Reset();
            }
        }
    }

    public long LastEventTicks
    {
        get { lock (_lock) return _lastEventTicks; }
    }

    public double LastIntervalSeconds
    {
        get { lock (_lock) return _lastIntervalSeconds; }
    }

    public string? ServerInstanceId
    {
        get { lock (_lock) return _serverInstanceId; }
    }

    public string? PublicModelId
    {
        get { lock (_lock) return _publicModelId; }
    }

    public IReadOnlyDictionary<string, string> ServerInfo
    {
        get { lock (_lock) return new Dictionary<string, string>(_serverInfo); }
    }

    public int ActiveRequestCount
    {
        get { lock (_lock) return _activeRequests.Count; }
    }

    public long CumulativePrefilled
    {
        get { lock (_lock) return _cumulativePrefilled; }
    }

    public long CumulativeGenerated
    {
        get { lock (_lock) return _cumulativeGenerated; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    private (uint Volume, ulong FileIndex)? _trackedFileId;
    private DateTime? _trackedCreationTime;
    private byte[]? _trackedHeaderBytes;

    public bool HasValidTelemetry
    {
        get { lock (_lock) return _hasThroughputBaseline || _serverInfo.Count > 0 || _serverInstanceId != null || _cumulativeGenerated > 0 || _cumulativePrefilled > 0; }
    }

    public bool IsIdle
    {
        get { lock (_lock) return HasValidTelemetry && _activeRequests.Count == 0 && (_running == 0 || _running == null); }
    }

    /// <summary>
    /// Reads newly appended bytes from the telemetry file and updates runtime state.
    /// Returns true if valid telemetry has been observed for the current server instance.
    /// </summary>
    public bool Poll(long nowTicks)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
            {
                return false;
            }

            try
            {
                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                // Detect file replacement/rotation:
                // 1. File index / volume on Windows
                // 2. Creation time
                // 3. Header prefix bytes (first up to 256 bytes)
                // 4. File truncation (fs.Length < _offset)
                byte[] currentHeader = new byte[256];
                int headerLen = fs.Read(currentHeader, 0, currentHeader.Length);

                (uint Volume, ulong FileIndex)? currentFileId = null;
                if (OperatingSystem.IsWindows())
                {
                    if (GetFileInformationByHandle(fs.SafeFileHandle, out var info))
                    {
                        ulong idx = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
                        currentFileId = (info.dwVolumeSerialNumber, idx);
                    }
                }
                DateTime currentCreationTime = File.GetCreationTimeUtc(_filePath);

                bool isReplaced = false;
                if (fs.Length < _offset)
                {
                    isReplaced = true;
                }
                else if (_trackedFileId.HasValue && currentFileId.HasValue &&
                         (_trackedFileId.Value.Volume != currentFileId.Value.Volume ||
                          _trackedFileId.Value.FileIndex != currentFileId.Value.FileIndex))
                {
                    isReplaced = true;
                }
                else if (!currentFileId.HasValue && _trackedCreationTime.HasValue &&
                         currentCreationTime != _trackedCreationTime.Value)
                {
                    isReplaced = true;
                }
                else if (_trackedHeaderBytes is not null)
                {
                    int compareLen = Math.Min(_trackedHeaderBytes.Length, headerLen);
                    if (headerLen < _trackedHeaderBytes.Length && fs.Length <= _trackedHeaderBytes.Length)
                    {
                        isReplaced = true;
                    }
                    else if (!currentHeader.AsSpan(0, compareLen).SequenceEqual(_trackedHeaderBytes.AsSpan(0, compareLen)))
                    {
                        isReplaced = true;
                    }
                }

                if (isReplaced)
                {
                    Reset();
                }

                _trackedFileId = currentFileId;
                _trackedCreationTime = currentCreationTime;
                if (_trackedHeaderBytes is null || isReplaced)
                {
                    _trackedHeaderBytes = new byte[headerLen];
                    Array.Copy(currentHeader, _trackedHeaderBytes, headerLen);
                }
                else if (headerLen > _trackedHeaderBytes.Length && _trackedHeaderBytes.Length < 256)
                {
                    _trackedHeaderBytes = new byte[headerLen];
                    Array.Copy(currentHeader, _trackedHeaderBytes, headerLen);
                }

                if (fs.Length == _offset)
                {
                    // No new bytes
                    return HasValidTelemetry || _activeRequests.Count > 0;
                }

                fs.Seek(_offset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

                string chunk = reader.ReadToEnd();
                _offset = fs.Position;

                if (string.IsNullOrEmpty(chunk))
                {
                    return HasValidTelemetry || _activeRequests.Count > 0;
                }

                string fullText = _remainder + chunk;
                _remainder = string.Empty;

                int lastNewline = fullText.LastIndexOf('\n');
                if (lastNewline < 0)
                {
                    // Incomplete single line; buffer until newline
                    _remainder = fullText;
                    return HasValidTelemetry || _activeRequests.Count > 0;
                }

                string completeChunk = fullText[..(lastNewline + 1)];
                if (lastNewline + 1 < fullText.Length)
                {
                    _remainder = fullText[(lastNewline + 1)..];
                }

                using var sr = new StringReader(completeChunk);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    ProcessLine(line, nowTicks);
                }

                return HasValidTelemetry || _activeRequests.Count > 0;
            }
            catch (IOException)
            {
                // File momentarily locked or inaccessible; soft fail
                return _hasThroughputBaseline;
            }
            catch (Exception)
            {
                return _hasThroughputBaseline;
            }
        }
    }

    private void ProcessLine(string line, long nowTicks)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("event", out var eventEl) || eventEl.ValueKind != JsonValueKind.String)
                return;

            string eventName = eventEl.GetString() ?? "";

            // Check server_instance_id
            if (root.TryGetProperty("server_instance_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String)
            {
                string sid = sidEl.GetString() ?? "";
                if (_serverInstanceId != null && !string.IsNullOrEmpty(sid) && !string.Equals(_serverInstanceId, sid, StringComparison.Ordinal))
                {
                    // Server restart with a new instance ID inside the same append-only file:
                    // re-initialize instance telemetry counters, but preserve file offset and cursor.
                    ResetTelemetryState();
                }
                _serverInstanceId = sid;
            }

            _lastEventTicks = nowTicks;

            switch (eventName)
            {
                case "server_start":
                    ParseServerStart(root);
                    break;
                case "throughput":
                    ParseThroughput(root);
                    break;
                case "request_start":
                    ParseRequestStart(root);
                    break;
                case "request_done":
                    ParseRequestDone(root);
                    break;
                case "request_rejected":
                case "request_error":
                    ParseRequestTerminated(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // Skip only malformed line
        }
    }

    private void ParseServerStart(JsonElement root)
    {
        if (root.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.Object)
        {
            if (server.TryGetProperty("public_model_id", out var mid) && mid.ValueKind == JsonValueKind.String)
                _publicModelId = mid.GetString();
        }
        else if (root.TryGetProperty("public_model_id", out var midTop) && midTop.ValueKind == JsonValueKind.String)
        {
            _publicModelId = midTop.GetString();
        }

        if (root.TryGetProperty("artifact", out var artifact) && artifact.ValueKind == JsonValueKind.Object)
        {
            if (artifact.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String)
                _serverInfo["Target"] = target.GetString() ?? "";
            if (artifact.TryGetProperty("weights_id", out var weights) && weights.ValueKind == JsonValueKind.String)
                _serverInfo["Weights"] = weights.GetString() ?? "";
        }

        if (root.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.Object)
        {
            if (engine.TryGetProperty("max_context", out var mc) && mc.ValueKind == JsonValueKind.Number)
                _serverInfo["Max Context"] = $"{mc.GetInt64()}";
            if (engine.TryGetProperty("kv_capacity", out var kvc) && kvc.ValueKind == JsonValueKind.Number)
                _serverInfo["KV Capacity"] = $"{kvc.GetInt64()}";
            if (engine.TryGetProperty("kv_capacity_mode", out var kvcm) && kvcm.ValueKind == JsonValueKind.String)
                _serverInfo["KV Mode"] = kvcm.GetString() ?? "";
            if (engine.TryGetProperty("max_concurrency", out var mconc) && mconc.ValueKind == JsonValueKind.Number)
                _serverInfo["Max Concurrency"] = $"{mconc.GetInt32()}";
            if (engine.TryGetProperty("kv_cache", out var kvcType) && kvcType.ValueKind == JsonValueKind.String)
                _serverInfo["KV Cache"] = kvcType.GetString() ?? "";
            if (engine.TryGetProperty("speculative_backend", out var sb) && sb.ValueKind == JsonValueKind.String)
            {
                _specBackend = sb.GetString();
                _serverInfo["Speculative"] = _specBackend ?? "none";
            }
            if (engine.TryGetProperty("speculative_draft_window", out var sdw) && sdw.ValueKind == JsonValueKind.Number)
            {
                _specDraftWindow = sdw.GetInt32();
                if (!string.IsNullOrEmpty(_specBackend) && _specBackend != "none")
                    _serverInfo["Draft Window"] = $"{_specDraftWindow}";
            }
            if (engine.TryGetProperty("log_stats_interval_ms", out var lsi) && lsi.ValueKind == JsonValueKind.Number)
            {
                _lastIntervalSeconds = lsi.GetDouble() / 1000.0;
            }
        }

        if (root.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            if (env.TryGetProperty("gpu_name", out var gpu) && gpu.ValueKind == JsonValueKind.String)
                _serverInfo["GPU"] = gpu.GetString() ?? "";
        }
    }

    private void ParseThroughput(JsonElement root)
    {
        double intervalSeconds = 0.0;
        if (root.TryGetProperty("interval_seconds", out var intEl) && intEl.ValueKind == JsonValueKind.Number)
        {
            intervalSeconds = intEl.GetDouble();
            if (intervalSeconds > 0.0) _lastIntervalSeconds = intervalSeconds;
        }

        long computedPrefill = 0;
        long committedDecode = 0;
        if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
        {
            if (tokens.TryGetProperty("computed_prefill", out var cp) && cp.ValueKind == JsonValueKind.Number)
                computedPrefill = cp.GetInt64();
            if (tokens.TryGetProperty("committed_decode", out var cd) && cd.ValueKind == JsonValueKind.Number)
                committedDecode = cd.GetInt64();
        }

        if (root.TryGetProperty("scheduler", out var sched) && sched.ValueKind == JsonValueKind.Object)
        {
            if (sched.TryGetProperty("running", out var run) && run.ValueKind == JsonValueKind.Number)
                _running = run.GetInt32();
            if (sched.TryGetProperty("waiting", out var wait) && wait.ValueKind == JsonValueKind.Number)
                _queued = wait.GetInt32();
        }

        // Throughput rates: calculate from raw deltas over interval as recommended by NInfer
        if (intervalSeconds > 0.0)
        {
            _prefillRate = (double)computedPrefill / intervalSeconds;
            _decodeRate = (double)committedDecode / intervalSeconds;
        }
        else if (root.TryGetProperty("throughput_tokens_per_second", out var tps) && tps.ValueKind == JsonValueKind.Object)
        {
            if (tps.TryGetProperty("prefill", out var pr) && pr.ValueKind == JsonValueKind.Number)
                _prefillRate = pr.GetDouble();
            if (tps.TryGetProperty("decode", out var dr) && dr.ValueKind == JsonValueKind.Number)
                _decodeRate = dr.GetDouble();
        }

        // Accumulate totals exactly once per throughput event
        _cumulativePrefilled += computedPrefill;
        _cumulativeGenerated += committedDecode;
        _hasThroughputBaseline = true;
    }

    private void ParseRequestStart(JsonElement root)
    {
        if (root.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object &&
            req.TryGetProperty("request_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            long id = idEl.GetInt64();
            _activeRequests[id] = new ActiveRequestState
            {
                Id = id,
                StartTime = DateTimeOffset.Now,
            };
        }
    }

    private void ParseRequestDone(JsonElement root)
    {
        long reqId = 0;
        if (root.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object &&
            req.TryGetProperty("request_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            reqId = idEl.GetInt64();
            _activeRequests.Remove(reqId);
        }

        // When all active requests have finished, update running and rate counters to idle
        if (_activeRequests.Count == 0)
        {
            _running = 0;
            _queued = 0;
            _prefillRate = 0.0;
            _decodeRate = 0.0;
        }

        // TTFT timing
        if (root.TryGetProperty("timings_seconds", out var timings) && timings.ValueKind == JsonValueKind.Object &&
            timings.TryGetProperty("ttft", out var ttftEl) && ttftEl.ValueKind == JsonValueKind.Number)
        {
            double ttftMs = ttftEl.GetDouble() * 1000.0;
            if (ttftMs >= 0.0 && double.IsFinite(ttftMs))
            {
                if (_ttftSamples.Count >= 10) _ttftSamples.RemoveAt(0);
                _ttftSamples.Add(ttftMs);
            }
        }

        // Speculative decoding metrics
        if (root.TryGetProperty("speculative", out var spec) && spec.ValueKind == JsonValueKind.Object)
        {
            if (spec.TryGetProperty("drafted_tokens", out var dt) && dt.ValueKind == JsonValueKind.Number)
                _specDraftedTokens += dt.GetInt64();
            if (spec.TryGetProperty("accepted_tokens", out var at) && at.ValueKind == JsonValueKind.Number)
                _specAcceptedTokens += at.GetInt64();
        }
    }

    private void ParseRequestTerminated(JsonElement root)
    {
        if (root.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object &&
            req.TryGetProperty("request_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            _activeRequests.Remove(idEl.GetInt64());
            if (_activeRequests.Count == 0)
            {
                _running = 0;
                _queued = 0;
                _prefillRate = 0.0;
                _decodeRate = 0.0;
            }
        }
    }

    /// <summary>
    /// Populates snapshot fields with parsed NInfer telemetry.
    /// </summary>
    public void PopulateSnapshot(MetricSnapshotBuilder builder)
    {
        lock (_lock)
        {
            bool isIdle = IsIdle;
            if (isIdle)
            {
                builder.Running = MetricValue<int>.Exact(0, MetricSource.NativeApi);
                builder.Queued = MetricValue<int>.Exact(0, MetricSource.NativeApi);
                builder.PrefillTokPerSec = MetricValue<double>.Exact(0.0, MetricSource.Derived, "idle");
                builder.GenerationTokPerSec = MetricValue<double>.Exact(0.0, MetricSource.Derived, "idle");
            }
            else
            {
                if (_running.HasValue)
                    builder.Running = MetricValue<int>.Exact(_running.Value, MetricSource.NativeApi);

                if (_queued.HasValue)
                    builder.Queued = MetricValue<int>.Exact(_queued.Value, MetricSource.NativeApi);

                if (_prefillRate.HasValue && double.IsFinite(_prefillRate.Value))
                    builder.PrefillTokPerSec = MetricValue<double>.Approx(_prefillRate.Value, MetricSource.Derived);

                if (_decodeRate.HasValue && double.IsFinite(_decodeRate.Value))
                    builder.GenerationTokPerSec = MetricValue<double>.Approx(_decodeRate.Value, MetricSource.Derived);
            }

            if (_hasThroughputBaseline)
            {
                builder.PrefilledTokensTotal = MetricValue<long>.Approx(_cumulativePrefilled, MetricSource.Derived, "since telemetry log start");
                builder.GeneratedTokensTotal = MetricValue<long>.Approx(_cumulativeGenerated, MetricSource.Derived, "since telemetry log start");
            }

            // Rolling TTFT
            if (_ttftSamples.Count > 0)
            {
                double avg = _ttftSamples.Average();
                string desc = _ttftSamples.Count == 10
                    ? "recent TTFT (last 10)"
                    : $"recent TTFT ({_ttftSamples.Count} requests)";
                builder.RecentTtftMs = MetricValue<double>.Exact(avg, MetricSource.NativeApi, desc);
            }


            // Active request rows
            if (_activeRequests.Count > 0)
            {
                var rows = new List<RequestSnapshot>(_activeRequests.Count);
                foreach (var req in _activeRequests.Values.OrderBy(r => r.Id))
                {
                    rows.Add(new RequestSnapshot
                    {
                        Id = $"#{req.Id}",
                        InputTokens = MetricValue<long>.None,
                        CachedTokens = MetricValue<long>.None,
                        PrefilledTokens = MetricValue<long>.None,
                        OutputTokens = MetricValue<long>.None,
                        PrefillTokensPerSecond = MetricValue<double>.None,
                        TokensPerSecond = MetricValue<double>.None,
                    });
                }
                builder.Requests = rows;
            }
            else if (_hasThroughputBaseline || HasValidTelemetry)
            {
                builder.Requests = Array.Empty<RequestSnapshot>();
            }

            // Info dictionary
            foreach (var kvp in _serverInfo)
            {
                builder.Info[kvp.Key] = kvp.Value;
            }

            if (_specDraftedTokens > 0)
            {
                double pct = 100.0 * (double)_specAcceptedTokens / (double)_specDraftedTokens;
                builder.Info["MTP Acceptance"] = $"{pct:F1}%";
            }
        }
    }

    /// <summary>
    /// Resets only the server instance telemetry state when a new server_instance_id is seen
    /// in the same physical append-only log file. File offset, remainder, and file identity are preserved.
    /// </summary>
    public void ResetTelemetryState()
    {
        lock (_lock)
        {
            _serverInstanceId = null;
            _running = null;
            _queued = null;
            _prefillRate = null;
            _decodeRate = null;
            _cumulativePrefilled = 0;
            _cumulativeGenerated = 0;
            _hasThroughputBaseline = false;
            _lastEventTicks = 0;
            _lastIntervalSeconds = 0.0;
            _ttftSamples.Clear();
            _activeRequests.Clear();
            _serverInfo.Clear();
            _publicModelId = null;
            _specDraftedTokens = 0;
            _specAcceptedTokens = 0;
            _specBackend = null;
            _specDraftWindow = 0;
        }
    }

    /// <summary>
    /// Full reset: resets both telemetry state and physical file state (cursor, remainder, file identity).
    /// Used when the file is truncated, replaced, or the file path changes.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            ResetTelemetryState();
            _offset = 0;
            _remainder = string.Empty;
            _trackedFileId = null;
            _trackedCreationTime = null;
            _trackedHeaderBytes = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Reset();
        }
    }
}

/// <summary>
/// Helper builder to populate MetricSnapshot without mutability violations.
/// </summary>
public sealed class MetricSnapshotBuilder
{
    public ConnectionState State { get; set; } = ConnectionState.Online;
    public BackendKind Kind { get; set; } = BackendKind.NInfer;
    public MetricValue<int> Running { get; set; } = MetricValue<int>.None;
    public MetricValue<int> Queued { get; set; } = MetricValue<int>.None;
    public MetricValue<double> PrefillTokPerSec { get; set; } = MetricValue<double>.None;
    public MetricValue<double> GenerationTokPerSec { get; set; } = MetricValue<double>.None;
    public MetricValue<double> KvCacheUsage { get; set; } = MetricValue<double>.None;
    public MetricValue<double> RecentTtftMs { get; set; } = MetricValue<double>.None;
    public MetricValue<long> GeneratedTokensTotal { get; set; } = MetricValue<long>.None;
    public MetricValue<long> PrefilledTokensTotal { get; set; } = MetricValue<long>.None;
    public IReadOnlyList<RequestSnapshot>? Requests { get; set; }
    public string? ModelName { get; set; }
    public IReadOnlyList<string> LoadedModels { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> Info { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MetricSnapshot Build() => new()
    {
        Timestamp = DateTimeOffset.Now,
        State = State,
        Kind = Kind,
        Running = Running,
        Queued = Queued,
        PrefillTokPerSec = PrefillTokPerSec,
        GenerationTokPerSec = GenerationTokPerSec,
        KvCacheUsage = KvCacheUsage,
        RecentTtftMs = RecentTtftMs,
        GeneratedTokensTotal = GeneratedTokensTotal,
        PrefilledTokensTotal = PrefilledTokensTotal,
        Requests = Requests,
        ModelName = ModelName,
        LoadedModels = LoadedModels,
        Info = Info,
    };
}
