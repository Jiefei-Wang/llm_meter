using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LLMMeter.Adapters;
using LLMMeter.Collection;
using LLMMeter.Core;

namespace LLMMeter.UI;

/// <summary>
/// Binds a shared BackendCollector to one widget. All values respect the
/// metric quality model: exact, ~approximate, or "—" unavailable.
/// </summary>
public sealed class MonitorWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private BackendCollector? _collector;
    private BackendRegistry.TargetEntry? _entry;
    private MetricSnapshot? _lastRendered;
    private bool _forceNext;
    private bool _renderingActive;
    private int _renderQueued;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, object?> _published = new();
    private readonly RateHistory _history = new();
    private readonly RateDisplayHold _prefillDisplay = new();
    private readonly RateDisplayHold _generateDisplay = new();
    private readonly RequestSlotList _requestSlots = new();
    private readonly DispatcherTimer _requestStateTimer;
    private bool _showingPrefillHistory = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BackendCollector? Collector => _collector;
    public TelemetryHelp? CurrentHelp { get; private set; }
    public bool IncludeDetails { get; set; }

    public ObservableCollection<RequestRow> RequestRows => _requestSlots.Rows;
    public IReadOnlyList<ActivityPoint> PrefillHistory { get; private set; } = Array.Empty<ActivityPoint>();
    public IReadOnlyList<ActivityPoint> GenerateHistory { get; private set; } = Array.Empty<ActivityPoint>();
    public IReadOnlyList<ActivityPoint> SelectedActivityHistory { get; private set; } = Array.Empty<ActivityPoint>();
    public string ActivityTitle { get; private set; } = "Prefill · last 5 minutes";
    public string ActivityCurrentText { get; private set; } = "—";

    // header
    public string HeaderText { get; private set; } = "LLM Meter";
    public string SubtitleText { get; private set; } = "select a backend";
    public string StatusToolTip { get; private set; } = "Connecting";
    public Brush StatusBrush { get; private set; } = Brushes.Gray;
    public string RunningText { get; private set; } = "—";
    public string GeneratedText { get; private set; } = "—";
    public string PrefillText { get; private set; } = "—";
    public string GenerateText { get; private set; } = "—";
    public string TtftText { get; private set; } = "—";
    public string KvText { get; private set; } = "—";
    public string PrefilledText { get; private set; } = "—";
    public string PrefilledToolTip { get; private set; } = "";
    public string MoreText { get; private set; } = "";
    public string ModelsInfoText { get; private set; } = "";
    public Visibility InfoButtonVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility RequestsAreaVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility ModelsTextVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility MoreTextVisibility { get; private set; } = Visibility.Collapsed;
    public string StateToolTip { get; private set; } = "";

    /// <summary>Tooltip text for the running/queued metric (explains x/y).</summary>
    public string RunningToolTip { get; private set; } = "";
    /// <summary>Tooltip text for the generated-total metric.</summary>
    public string GeneratedToolTip { get; private set; } = "";

    public MonitorWindowViewModel()
    {
        _requestStateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _requestStateTimer.Tick += (_, _) =>
        {
            _requestSlots.Advance(DateTimeOffset.Now);
            if (!_requestSlots.HasTimedState) _requestStateTimer.Stop();
        };
    }

    /// <summary>Attach to a collector (shared — no duplicate polling).</summary>
    public void Bind(BackendCollector collector, BackendRegistry.TargetEntry entry)
    {
        if (ReferenceEquals(_collector, collector))
        {
            _entry = entry;
            UpdateHeader(entry.ModelName);
            PollLatest(force: true);
            return;
        }
        if (_collector != null) _collector.SnapshotUpdated -= OnSnapshotUpdated;
        _collector = collector;
        _entry = entry;
        _history.Clear();
        _prefillDisplay.Reset();
        _generateDisplay.Reset();
        _requestSlots.Clear();
        _lastRendered = null;
        _forceNext = true;
        CurrentHelp = collector.GetHelp();
        UpdateHeader(entry.ModelName);
        collector.SnapshotUpdated += OnSnapshotUpdated;
        if (collector.Latest is { } latest) _history.Record(latest);
        Log.Info($"VM bound to {collector.Endpoint.Id}");
        PollLatest(force: true);
    }

    public void Unbind()
    {
        if (_collector != null) _collector.SnapshotUpdated -= OnSnapshotUpdated;
        _collector = null;
        _entry = null;
        _lastRendered = null;
        CurrentHelp = null;
        _history.Clear();
        _prefillDisplay.Reset();
        _generateDisplay.Reset();
        HeaderText = "LLM Meter";
        SubtitleText = "select a backend";
        Render(null);
    }

    public void ShowScanning()
    {
        HeaderText = "LLM Meter";
        SubtitleText = "scanning for servers…";
        P(nameof(HeaderText));
        P(nameof(SubtitleText));
    }

    public void SetRenderingActive(bool active)
    {
        _renderingActive = active;
        if (active) PollLatest(force: true);
    }

    private void OnSnapshotUpdated(MetricSnapshot snapshot)
    {
        _history.Record(snapshot);
        if (!_renderingActive || Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            if (_renderingActive) PollLatest(force: true);
        });
    }

    public void PollLatest(bool force = false) => Poll(force || _forceNext);

    private void Poll(bool force)
    {
        _forceNext = false;
        var s = _collector?.Latest;
        if (!force && ReferenceEquals(s, _lastRendered)) return;
        if (s is null && _lastRendered is null) return;

        _lastRendered = s;
        Render(s);
    }

    private void Render(MetricSnapshot? s)
    {
        if (s is null)
        {
            SetState(ConnectionState.Connecting, null);
            ResetMetrics();
            RaiseAll();
            return;
        }

        SetState(s.State, s);
        CurrentHelp = _collector?.GetHelp();
        UpdateHeader(s.ModelName);

        RunningText = MetricRunningQueue(s);
        PrefillText = MetricRate(_prefillDisplay.Update(s.PrefillTokPerSec, s.Timestamp), "prefill");
        GenerateText = MetricRate(_generateDisplay.Update(s.GenerationTokPerSec, s.Timestamp), "generation");
        GeneratedText = MetricGeneratedTotal(s);

        TtftText = s.RecentTtftMs.HasValue ? Fmt.Metric(s.RecentTtftMs, Fmt.Milliseconds) : "—";
        KvText = s.KvCacheUsage.HasValue ? Fmt.Metric(s.KvCacheUsage, Fmt.Percent) : "—";
        PrefilledText = MetricPrefilledTotal(s);
        PrefillHistory = _history.PrefillSnapshot(DateTimeOffset.Now);
        GenerateHistory = _history.GenerateSnapshot(DateTimeOffset.Now);
        UpdateSelectedActivity();

        // Requests area (expanded). Request slots are stable by ID: completed
        // work lingers, holes remain in place, and new work reuses holes first.
        MoreText = "";
        MoreTextVisibility = Visibility.Collapsed;

        bool enumerationSupported = s.Requests != null;
        RequestsAreaVisibility = enumerationSupported ? Visibility.Visible : Visibility.Collapsed;

        if (enumerationSupported)
        {
            _requestSlots.Update(s.Requests!, DateTimeOffset.Now);
            if (_requestSlots.HasTimedState && !_requestStateTimer.IsEnabled)
                _requestStateTimer.Start();
        }
        else
        {
            // Enumeration unsupported: show honest aggregate note when running > 0.
            if (s.Running.HasValue && s.Running.Value > 0 && IncludeDetails)
            {
                _requestSlots.ShowMessages([
                    $"{Fmt.Count(s.Running.Value)} active request(s)",
                    "per-request details unavailable",
                ]);
            }
            else
            {
                RequestsAreaVisibility = Visibility.Collapsed;
                _requestSlots.Clear();
            }
        }

        // Loaded-model info lines (LM Studio / Ollama / generic)
        ModelsInfoText = BuildModelsLine(s);
        ModelsTextVisibility = ModelsInfoText.Length > 0 && IncludeDetails
            ? Visibility.Visible : Visibility.Collapsed;

        InfoButtonVisibility =
            s.State == ConnectionState.Limited ? Visibility.Visible : Visibility.Collapsed;

        RaiseAll();
    }

    public void SelectActivityHistory(bool prefill)
    {
        _showingPrefillHistory = prefill;
        UpdateSelectedActivity();
        P(nameof(SelectedActivityHistory));
        P(nameof(ActivityTitle));
        P(nameof(ActivityCurrentText));
    }

    private void UpdateSelectedActivity()
    {
        SelectedActivityHistory = _showingPrefillHistory ? PrefillHistory : GenerateHistory;
        ActivityTitle = (_showingPrefillHistory ? "Prefill" : "Generate") + " · last 5 minutes";
        ActivityCurrentText = _showingPrefillHistory ? PrefillText : GenerateText;
    }

    private void UpdateHeader(string? modelName)
    {
        if (_entry is not { } entry) return;
        string name = modelName is { Length: > 0 }
            ? $"{entry.Target.Kind.DisplayName()} · {modelName}"
            : entry.Target.DisplayName;
        HeaderText = name.Length > 42 ? name[..41].TrimEnd() + "…" : name;
        SubtitleText = MonitorWindow.DescribeOrigin(entry);
    }

    private void ResetMetrics()
    {
        RunningText = GeneratedText = PrefillText = GenerateText = TtftText = KvText = PrefilledText = "—";
        RunningToolTip = GeneratedToolTip = PrefilledToolTip = "";
        MoreText = ModelsInfoText = "";
        PrefillHistory = GenerateHistory = SelectedActivityHistory = Array.Empty<ActivityPoint>();
        ActivityCurrentText = "—";
        _requestSlots.Clear();
        _requestStateTimer.Stop();
        InfoButtonVisibility = RequestsAreaVisibility = ModelsTextVisibility = MoreTextVisibility = Visibility.Collapsed;
    }

    internal static string BuildModelsLine(MetricSnapshot s)
    {
        if (s.LoadedModels.Count == 0) return "";
        var shown = s.LoadedModels.Take(3).Select(m => m.Length > 26 ? m[..25] + "…" : m);
        var line = string.Join(" · ", shown);
        if (s.LoadedModels.Count > 3) line += $" +{s.LoadedModels.Count - 3}";
        return line;
    }

    private void SetState(ConnectionState state, MetricSnapshot? s)
    {
        StatusBrush = state switch
        {
            ConnectionState.Online => App.ThemedBrush("OkBrush"),
            ConnectionState.Limited => App.ThemedBrush("BusyBrush"),
            ConnectionState.Connecting => App.ThemedBrush("DimBrush"),
            _ => App.ThemedBrush("OfflineBrush"),
        };
        StatusToolTip = state switch
        {
            ConnectionState.Online => "Online",
            ConnectionState.Limited => "Limited telemetry (ⓘ for details)",
            ConnectionState.Connecting => "Connecting…",
            _ => "Offline",
        };
        StateToolTip = s?.Info.TryGetValue("Mode", out var mode) == true ? mode : "";
    }

    internal static string MetricInt(MetricValue<int> v, string what)
    {
        if (!v.HasValue) return "—";
        return v.Quality == MetricQuality.Exact ? Fmt.Count(v.Value) : "~" + Fmt.Count(v.Value);
    }

    internal static string MetricRate(MetricValue<double> v, string what) =>
        v.HasValue ? Fmt.Rate(v.Value) : "—";

    /// <summary>Running/queued as "x/y". Queued unavailable → "x/—".</summary>
    internal string MetricRunningQueue(MetricSnapshot s)
    {
        string run = s.Running.HasValue ? s.Running.Value.ToString() : "—";
        string queued = MetricInt(s.Queued, "queued");
        RunningToolTip = s.Running.HasValue && s.Queued.HasValue
            ? $"{s.Running.Value} running / {s.Queued.Value} queued"
            : s.Running.HasValue
                ? $"{s.Running.Value} running (queue unavailable)"
                : "no activity data";
        return $"{run}/{queued}";
    }

    /// <summary>Cumulative generated tokens, compact K/M units.</summary>
    internal string MetricGeneratedTotal(MetricSnapshot s)
    {
        if (!s.GeneratedTokensTotal.HasValue) { GeneratedToolTip = "—"; return "—"; }
        long v = s.GeneratedTokensTotal.Value;
        GeneratedToolTip = $"{v:N0} tokens generated since monitoring began";
        return Fmt.Tokens(v);
    }

    /// <summary>Cumulative prefilled prompt tokens, compact K/M units.</summary>
    internal string MetricPrefilledTotal(MetricSnapshot s)
    {
        if (!s.PrefilledTokensTotal.HasValue) { PrefilledToolTip = "—"; return "—"; }
        long v = s.PrefilledTokensTotal.Value;
        PrefilledToolTip = $"{v:N0} prompt tokens prefilled since monitoring began";
        return Fmt.Tokens(v);
    }

    private void RaiseAll()
    {
        Changed(nameof(HeaderText), HeaderText); Changed(nameof(SubtitleText), SubtitleText);
        Changed(nameof(StatusBrush), StatusBrush); Changed(nameof(StatusToolTip), StatusToolTip);
        Changed(nameof(RunningText), RunningText); Changed(nameof(GeneratedText), GeneratedText);
        Changed(nameof(RunningToolTip), RunningToolTip); Changed(nameof(GeneratedToolTip), GeneratedToolTip);
        Changed(nameof(PrefillText), PrefillText); Changed(nameof(GenerateText), GenerateText);
        Changed(nameof(TtftText), TtftText); Changed(nameof(KvText), KvText);
        Changed(nameof(PrefilledText), PrefilledText); Changed(nameof(PrefilledToolTip), PrefilledToolTip);
        Changed(nameof(MoreText), MoreText); Changed(nameof(ModelsInfoText), ModelsInfoText);
        Changed(nameof(InfoButtonVisibility), InfoButtonVisibility); Changed(nameof(RequestsAreaVisibility), RequestsAreaVisibility);
        Changed(nameof(ModelsTextVisibility), ModelsTextVisibility); Changed(nameof(MoreTextVisibility), MoreTextVisibility);
        Changed(nameof(StateToolTip), StateToolTip);
        Changed(nameof(PrefillHistory), PrefillHistory); Changed(nameof(GenerateHistory), GenerateHistory);
        Changed(nameof(SelectedActivityHistory), SelectedActivityHistory);
        Changed(nameof(ActivityTitle), ActivityTitle); Changed(nameof(ActivityCurrentText), ActivityCurrentText);
    }

    private void Changed(string name, object? value)
    {
        if (_published.TryGetValue(name, out var previous) && Equals(previous, value)) return;
        _published[name] = value;
        P(name);
    }

    private void P(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_collector != null) _collector.SnapshotUpdated -= OnSnapshotUpdated;
        _collector = null;
        _renderingActive = false;
        _requestStateTimer.Stop();
    }
}
