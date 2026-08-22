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

    private readonly EndpointRef _endpoint;
    private readonly Func<BackendKind, IBackendAdapter> _adapterFactory;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _cts = new();

    private IBackendAdapter? _adapter;
    private MetricSnapshot? _latest;
    private long _lastSuccessTicks;
    private int _consecutiveFailures;
    private Task _loop = null!;

    public event Action<MetricSnapshot>? SnapshotUpdated;

    public BackendCollector(EndpointRef endpoint, BackendKind? knownKind)
        : this(endpoint, knownKind, kind => CreateAdapter(kind))
    {
    }

    internal BackendCollector(EndpointRef endpoint, BackendKind? knownKind,
        Func<BackendKind, IBackendAdapter> adapterFactory)
    {
        _endpoint = endpoint;
        _adapterFactory = adapterFactory;
        _client = SharedClientFactory.Create();
        _client.Timeout = PollTimeout;
        KnownKind = knownKind;
    }

    public EndpointRef Endpoint => _endpoint;
    public BackendKind? KnownKind { get; private set; }
    public MetricSnapshot? Latest => _latest;

    private static IBackendAdapter CreateAdapter(BackendKind kind) => kind switch
    {
        BackendKind.Vllm => new VllmAdapter(),
        BackendKind.LlamaCpp => new LlamaCppAdapter(),
        BackendKind.LmStudio => new LmStudioAdapter(),
        BackendKind.Ollama => new OllamaAdapter(),
        BackendKind.GenericOpenAi => new GenericOpenAiAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Llama adapter command-line injection point (help popup).</summary>
    public void SetCurrentCommand(string cmd)
    {
        if (_adapter is LlamaCppAdapter l) l.SetCurrentCommand(cmd);
    }

    public TelemetryHelp? GetHelp() => _adapter?.GetHelp();

    public BackendCapabilities Capabilities => _adapter?.Capabilities ?? BackendCapabilities.None;

    public void Start() => _loop = Task.Run(() => PollLoopAsync(_cts.Token));

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // If kind unknown (shouldn't happen — registry fingerprints first), try to detect.
        if (KnownKind is null or BackendKind.Unknown)
        {
            var fp = await new Discovery.EndpointFingerprinter()
                .FingerprintAsync(_endpoint.BaseUrl, ct).ConfigureAwait(false);
            if (fp.Kind == BackendKind.Unknown && ct.IsCancellationRequested) return;
            KnownKind = fp.Kind == BackendKind.Unknown ? BackendKind.GenericOpenAi : fp.Kind;
        }

        _adapter ??= _adapterFactory(KnownKind.Value);

        while (!ct.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                using var http = new PollHttp(_client, _endpoint.BaseUrl, PollTimeout);
                var snapshot = await _adapter.CollectAsync(http, ct).ConfigureAwait(false);

                if (snapshot.State != ConnectionState.Offline)
                {
                    _consecutiveFailures = 0;
                    _lastSuccessTicks = MonoClock.NowTicks;
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

    private void Publish(MetricSnapshot s)
    {
        _latest = s;
        SnapshotUpdated?.Invoke(s);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _client.Dispose();
        _cts.Dispose();
    }

    /// <summary>Non-owning IHttp over the collector's shared client.</summary>
    private sealed class PollHttp(HttpClient client, Uri baseUrl, TimeSpan timeout) : IHttp, IDisposable
    {
        public Uri BaseUrl { get; } = HttpService.NormalizeBase(baseUrl);
        public TimeSpan Timeout { get; } = timeout;

        public async Task<(int Status, string Body)> GetStringAsync(string path, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(Timeout);
            try
            {
                using var resp = await client.GetAsync(new Uri(BaseUrl, path), HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
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
