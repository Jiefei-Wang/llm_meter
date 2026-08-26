using System.Net.Http;
using LLMMeter.Adapters;
using LLMMeter.Core;

namespace LLMMeter.Collection;

/// <summary>
/// One collector per endpoint. Every monitor window observing that endpoint
/// shares this instance — exactly one HTTP polling stream regardless of how
/// many windows display it.
/// </summary>
public sealed class BackendCollector : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan[] BackoffSchedule = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private EndpointRef _endpoint;
    private readonly Func<BackendKind, IBackendAdapter> _adapterFactory;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _adapterLock = new();

    private volatile string? _authToken;
    private IBackendAdapter? _adapter;
    private MetricSnapshot? _latest;
    private ConnectionState? _lastLoggedState;
    private int _consecutiveFailures;
    private Task _loop = null!;
    private int _started;
    private int _disposed;

    public event Action<MetricSnapshot>? SnapshotUpdated;

    public BackendCollector(EndpointRef endpoint, BackendKind? knownKind, string? modelId = null)
        : this(endpoint, knownKind, kind => CreateAdapter(kind, endpoint, modelId), endpoint.AuthToken)
    {
        ModelId = modelId;
    }

    internal BackendCollector(EndpointRef endpoint, BackendKind? knownKind,
        Func<BackendKind, IBackendAdapter> adapterFactory, string? authToken = null)
    {
        _endpoint = endpoint;
        _adapterFactory = adapterFactory;
        _authToken = authToken ?? endpoint.AuthToken;
        _client = SharedClientFactory.Create();
        _client.Timeout = PollTimeout;
        KnownKind = knownKind;
    }

    public EndpointRef Endpoint => _endpoint;
    public string? ModelId { get; }
    public BackendKind? KnownKind { get; private set; }
    public BackendKind EffectiveKind
    {
        get
        {
            lock (_adapterLock)
                return _adapter?.Kind ?? KnownKind ?? BackendKind.Unknown;
        }
    }
    public MetricSnapshot? Latest => Volatile.Read(ref _latest);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Reconfigure(EndpointRef endpoint)
    {
        _endpoint = endpoint;
        _authToken = endpoint.AuthToken;
    }

    public void ChangeKind(BackendKind newKind)
    {
        if (newKind == BackendKind.Unknown || KnownKind == newKind) return;
        lock (_adapterLock)
        {
            if (KnownKind == newKind) return;
            KnownKind = newKind;
            var oldAdapter = _adapter;
            _adapter = _adapterFactory(newKind);
            if (oldAdapter is IDisposable d)
            {
                try { d.Dispose(); } catch { }
            }
            _lastLoggedState = null;
            _consecutiveFailures = 0;
            Publish(new MetricSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                State = ConnectionState.Connecting,
                Kind = newKind,
                ModelName = null,
                Info = new Dictionary<string, string> { ["Status"] = $"switched to {newKind.DisplayName()}" }
            });
        }
    }

    public void MarkOffline(string reason = "Endpoint unavailable")
    {
        var kind = EffectiveKind;
        if (kind == BackendKind.Unknown) kind = BackendKind.GenericOpenAi;
        var snap = new MetricSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            State = ConnectionState.Offline,
            Kind = kind,
            Requests = null,
            Info = new Dictionary<string, string> { ["Status"] = reason }
        };
        Publish(snap);
    }

    private static IBackendAdapter CreateAdapter(BackendKind kind, EndpointRef endpoint, string? modelId = null) => kind switch
    {
        BackendKind.Vllm => new VllmAdapter(),
        BackendKind.LlamaCpp => new LlamaCppAdapter(modelId),
        BackendKind.LmStudio => new LmStudioAdapter(),
        BackendKind.Ollama => new OllamaAdapter(),
        BackendKind.GenericOpenAi => new GenericOpenAiAdapter(),
        BackendKind.NInfer => new NInferAdapter(endpoint),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Llama / NInfer adapter command-line injection point (help popup).</summary>
    public void SetCurrentCommand(string cmd)
    {
        lock (_adapterLock)
        {
            if (_adapter is LlamaCppAdapter l) l.SetCurrentCommand(cmd);
            else if (_adapter is NInferAdapter n) n.SetCurrentCommand(cmd);
        }
    }

    public TelemetryHelp? GetHelp()
    {
        lock (_adapterLock) return _adapter?.GetHelp();
    }

    public BackendCapabilities Capabilities
    {
        get
        {
            lock (_adapterLock) return _adapter?.Capabilities ?? BackendCapabilities.None;
        }
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // If kind unknown (shouldn't happen — registry fingerprints first), try to detect.
        // Use the collector's authenticated client to preserve any configured Bearer token.
        if (KnownKind is null or BackendKind.Unknown)
        {
            using var probeHttp = new PollHttp(_client, _endpoint.BaseUrl, PollTimeout, _authToken);
            var fp = await new Discovery.EndpointFingerprinter(_ => probeHttp)
                .FingerprintAsync(_endpoint.BaseUrl, ct).ConfigureAwait(false);
            if (fp.Kind == BackendKind.Unknown && ct.IsCancellationRequested) return;
            KnownKind = fp.Kind == BackendKind.Unknown ? BackendKind.GenericOpenAi : fp.Kind;
        }

        lock (_adapterLock)
        {
            _adapter ??= _adapterFactory(KnownKind.Value);
        }

        while (!ct.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                IBackendAdapter adapter;
                lock (_adapterLock)
                {
                    _adapter ??= _adapterFactory(KnownKind.Value);
                    adapter = _adapter;
                }

                using var http = new PollHttp(_client, _endpoint.BaseUrl, PollTimeout, _authToken);
                var snapshot = await adapter.CollectAsync(http, ct).ConfigureAwait(false);
                if (snapshot.State != _lastLoggedState)
                {
                    Log.Info($"{_endpoint.Id} state {(_lastLoggedState?.ToString() ?? "first")} -> {snapshot.State}");
                    _lastLoggedState = snapshot.State;
                }

                if (snapshot.State != ConnectionState.Offline)
                {
                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                }

                Publish(snapshot);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                _consecutiveFailures++;
                Publish(MetricSnapshot.Offline(KnownKind!.Value));
            }

            if (_consecutiveFailures > 0)
            {
                int idx = Math.Min(_consecutiveFailures - 1, BackoffSchedule.Length - 1);
                delay = BackoffSchedule[idx];
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void Publish(MetricSnapshot s)
    {
        Volatile.Write(ref _latest, s);
        var handler = SnapshotUpdated;
        if (handler is null) return;

        foreach (var invocation in handler.GetInvocationList())
        {
            try
            {
                ((Action<MetricSnapshot>)invocation).Invoke(s);
            }
            catch (Exception ex)
            {
                Log.Warn($"subscriber exception ignored during snapshot update: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        SnapshotUpdated = null;
        if (_loop is null || _loop.IsCompleted)
            Cleanup();
        else
            _ = _loop.ContinueWith(_ => Cleanup(), TaskScheduler.Default);

        void Cleanup()
        {
            if (_adapter is IDisposable d) d.Dispose();
            _client.Dispose();
            _cts.Dispose();
        }

    }

    /// <summary>Non-owning IHttp over the collector's shared client.</summary>
    private sealed class PollHttp(HttpClient client, Uri baseUrl, TimeSpan timeout, string? authToken = null) : IHttp, IDisposable
    {
        public Uri BaseUrl { get; } = HttpService.NormalizeBase(baseUrl);
        public TimeSpan Timeout { get; } = timeout;

        public async Task<(int Status, string Body)> GetStringAsync(string path, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(Timeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl, path));
                if (!string.IsNullOrWhiteSpace(authToken))
                {
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken.Trim());
                }

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return ((int)resp.StatusCode, string.Empty);
                var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                return ((int)resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (-1, string.Empty);
            }
            catch (HttpRequestException)
            {
                return (-1, string.Empty);
            }
        }

        public void Dispose()
        {
        }
    }
}
