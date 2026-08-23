using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using LLMMeter.Adapters;
using LLMMeter.Collection;
using LLMMeter.Core;

namespace LLMMeter.UI;

/// <summary>
/// Binds a shared BackendCollector to one widget. All values respect the
/// metric quality model: exact, ~approximate, or "—" unavailable.
/// </summary>
public sealed class MonitorWindowViewModel : INotifyPropertyChanged
{
    private BackendCollector? _collector;
    private MetricSnapshot? _lastRendered;
    private bool _forceNext;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BackendCollector? Collector => _collector;
    public TelemetryHelp? CurrentHelp { get; private set; }
    public bool IncludeDetails { get; set; }

    public ObservableCollection<RequestRow> RequestRows { get; } = [];

    // header
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

    /// <summary>Attach to a collector (shared — no duplicate polling).</summary>
    public void Bind(BackendCollector collector, BackendRegistry.TargetEntry entry)
    {
        if (ReferenceEquals(_collector, collector))
        {
            PollLatest(force: true);
            return;
        }
        _collector = collector;
        _lastRendered = null;
        _forceNext = true;
        CurrentHelp = collector.GetHelp();
        Log.Info($"VM bound to {collector.Endpoint.Id}");
        PollLatest(force: true);
    }

    public void Unbind()
    {
        _collector = null;
        _lastRendered = null;
        CurrentHelp = null;
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
            return;
        }

        SetState(s.State, s);

        RunningText = MetricRunningQueue(s);
        PrefillText = MetricRate(s.PrefillTokPerSec, "prefill");
        GenerateText = MetricRate(s.GenerationTokPerSec, "generation");
        GeneratedText = MetricGeneratedTotal(s);

        TtftText = s.RecentTtftMs.HasValue ? Fmt.Metric(s.RecentTtftMs, Fmt.Milliseconds) : "—";
        KvText = s.KvCacheUsage.HasValue ? Fmt.Metric(s.KvCacheUsage, Fmt.Percent) : "—";
        PrefilledText = MetricPrefilledTotal(s);

        // Requests area (expanded)
        RequestRows.Clear();
        MoreText = "";
        MoreTextVisibility = Visibility.Collapsed;

        bool enumerationSupported = s.Requests != null;
        RequestsAreaVisibility = enumerationSupported ? Visibility.Visible : Visibility.Collapsed;

        if (enumerationSupported)
        {
            var reqs = s.Requests!;
            foreach (var r in reqs.Take(4))
                RequestRows.Add(RequestRow.From(r));
            if (reqs.Count > 4)
            {
                MoreText = $"+ {reqs.Count - 4} more";
                MoreTextVisibility = Visibility.Visible;
            }
            if (reqs.Count == 0)
            {
                RequestRows.Add(new RequestRow { Line = "no active request details" });
            }
        }
        else
        {
            // Enumeration unsupported: show honest aggregate note when running > 0.
            if (s.Running.HasValue && s.Running.Value > 0 && IncludeDetails)
            {
                RequestRows.Add(new RequestRow { Line = $"{Fmt.Count(s.Running.Value)} active request(s)" });
                RequestRows.Add(new RequestRow { Line = "per-request details unavailable" });
            }
            else
            {
                RequestsAreaVisibility = Visibility.Collapsed;
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
        v.HasValue ? Fmt.Metric(v, Fmt.Rate) : "—";

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
        P(nameof(StatusBrush)); P(nameof(StatusToolTip));
        P(nameof(RunningText)); P(nameof(GeneratedText));
        P(nameof(RunningToolTip)); P(nameof(GeneratedToolTip));
        P(nameof(PrefillText)); P(nameof(GenerateText));
        P(nameof(TtftText)); P(nameof(KvText));
        P(nameof(PrefilledText)); P(nameof(PrefilledToolTip));
        P(nameof(MoreText)); P(nameof(ModelsInfoText));
        P(nameof(InfoButtonVisibility)); P(nameof(RequestsAreaVisibility));
        P(nameof(ModelsTextVisibility)); P(nameof(MoreTextVisibility));
        P(nameof(StateToolTip));

        // ObservableCollection handles rows; nudge for safety on full rebind
        P(nameof(RequestRows));
    }

    private void P(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RequestRow
{
    public required string Line { get; init; }

    public static RequestRow From(RequestSnapshot r)
    {
        string inTok = r.InputTokens.HasValue ? Fmt.Tokens(r.InputTokens.Value) : "—";
        string outTok = r.OutputTokens.HasValue ? Fmt.Tokens(r.OutputTokens.Value) : "—";
        string rate = r.TokensPerSecond.HasValue
            ? (r.TokensPerSecond.Quality == MetricQuality.Exact ? "" : "~") + Fmt.Rate(r.TokensPerSecond.Value)
            : "";

        string idPart = r.Id.PadLeft(6);
        string inPart = ("IN " + inTok).PadRight(10);
        string outPart = ("OUT " + outTok).PadRight(9);
        string ratePart = rate.Length > 0 ? rate : "";
        return new RequestRow { Line = $"{idPart}  {inPart}{outPart}{ratePart}" };
    }
}
