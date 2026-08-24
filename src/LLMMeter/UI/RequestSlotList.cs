using System.Collections.ObjectModel;
using System.ComponentModel;
using LLMMeter.Core;

namespace LLMMeter.UI;

/// <summary>
/// Maintains stable visual request slots. Requests never shift when an earlier
/// request finishes; new work reuses the first truly empty slot.
/// </summary>
internal sealed class RequestSlotList
{
    internal static readonly TimeSpan CompletedRetention = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan EmptyRetention = TimeSpan.FromSeconds(10);

    public ObservableCollection<RequestRow> Rows { get; } = [];

    public void Update(IReadOnlyList<RequestSnapshot> requests, DateTimeOffset now)
    {
        var incoming = new Dictionary<string, RequestSnapshot>(StringComparer.Ordinal);
        foreach (var request in requests)
            incoming[request.Id] = request;

        foreach (var row in Rows)
        {
            if (row.RequestId is { } id && incoming.Remove(id, out var request))
                row.Activate(request);
            else if (row.IsActive)
                row.Complete(now + CompletedRetention);
        }

        Advance(now);

        // Preserve server order for requests which do not already own a slot.
        foreach (var request in requests)
        {
            if (!incoming.Remove(request.Id)) continue;
            var row = Rows.FirstOrDefault(candidate => candidate.IsEmpty);
            if (row == null)
            {
                row = RequestRow.Empty(now);
                Rows.Add(row);
            }
            row.Activate(request);
        }
    }

    public void Advance(DateTimeOffset now)
    {
        foreach (var row in Rows)
            if (row.IsCompleted && row.CompletedUntil <= now)
                row.MakeEmpty(row.CompletedUntil);

        // Only trailing empty rows collapse. Interior holes remain stable and
        // are preferred when the next request arrives.
        while (Rows.LastOrDefault() is { IsEmpty: true } last &&
               now - last.EmptySince >= EmptyRetention)
            Rows.RemoveAt(Rows.Count - 1);
    }

    public bool HasTimedState => Rows.Any(row => row.IsCompleted) || Rows.LastOrDefault()?.IsEmpty == true;

    public void ShowMessages(IEnumerable<string> messages)
    {
        var lines = messages.ToArray();
        if (Rows.Count == lines.Length && Rows.Select(row => row.Line).SequenceEqual(lines)) return;
        Rows.Clear();
        foreach (string line in lines) Rows.Add(RequestRow.Message(line));
    }

    public void Clear() => Rows.Clear();
}

public sealed class RequestRow : INotifyPropertyChanged
{
    private string _primaryText = "";
    private string _speedText = "";
    private string _metricsText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? RequestId { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsInformational { get; private set; }
    public bool IsEmpty => RequestId == null && !IsCompleted && !IsInformational;
    internal DateTimeOffset CompletedUntil { get; private set; }
    internal DateTimeOffset EmptySince { get; private set; }

    public string PrimaryText
    {
        get => _primaryText;
        private set { if (_primaryText == value) return; _primaryText = value; Changed(nameof(PrimaryText)); Changed(nameof(Line)); }
    }

    public string MetricsText
    {
        get => _metricsText;
        private set { if (_metricsText == value) return; _metricsText = value; Changed(nameof(MetricsText)); Changed(nameof(Line)); }
    }

    public string SpeedText
    {
        get => _speedText;
        private set { if (_speedText == value) return; _speedText = value; Changed(nameof(SpeedText)); Changed(nameof(Line)); }
    }

    // Retained for diagnostics/tests and accessibility tooling.
    public string Line => string.Join("  ", new[] { PrimaryText, SpeedText, MetricsText }.Where(text => text.Length > 0));

    internal static RequestRow Empty(DateTimeOffset since) => new() { EmptySince = since };
    internal static RequestRow Message(string text) => new() { IsInformational = true, PrimaryText = text };

    internal void Activate(RequestSnapshot request)
    {
        RequestId = request.Id;
        IsActive = true;
        IsCompleted = false;
        IsInformational = false;
        PrimaryText = request.Id;
        SpeedText = request.TokensPerSecond.HasValue ? CompactRate(request.TokensPerSecond.Value) : "—";
        MetricsText = FormatMetrics(request);
    }

    internal void Complete(DateTimeOffset until)
    {
        IsActive = false;
        IsCompleted = true;
        CompletedUntil = until;
        PrimaryText = $"{RequestId}  ·  completed";
        SpeedText = "";
    }

    internal void MakeEmpty(DateTimeOffset since)
    {
        RequestId = null;
        IsActive = false;
        IsCompleted = false;
        IsInformational = false;
        EmptySince = since;
        PrimaryText = "";
        SpeedText = "";
        MetricsText = "";
    }

    private static string FormatMetrics(RequestSnapshot request)
    {
        string input = Slot(request.InputTokens.HasValue ? Fmt.Tokens(request.InputTokens.Value) : "—");
        string cached = Slot(request.CachedTokens.HasValue ? Fmt.Tokens(request.CachedTokens.Value) : "—");
        string evaluated = Slot(request.PrefilledTokens.HasValue ? Fmt.Tokens(request.PrefilledTokens.Value) : "—");
        string output = Slot(request.OutputTokens.HasValue ? Fmt.Tokens(request.OutputTokens.Value) : "—");
        return $"IN {input} · CACHED {cached} · EVAL {evaluated} · OUT {output}";

        static string Slot(string value) => value.PadRight(6);
    }

    private static string CompactRate(double value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.#}M/s",
        >= 10_000 => $"{value / 1000:0}k/s",
        >= 1_000 => $"{value / 1000:0.#}k/s",
        _ => Fmt.Rate(value),
    };

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
