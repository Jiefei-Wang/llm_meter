using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LLMMeter.Core;
using WindowStateConfig = LLMMeter.Persistence.WindowConfig;

namespace LLMMeter.UI;

public partial class MonitorWindow : Window
{
    public static readonly double[] ScaleSteps = [0.75, 0.90, 1.00, 1.10, 1.25, 1.50, 1.75, 2.00];

    private readonly WidgetManager _manager;
    private readonly MonitorWindowViewModel _vm = new();
    private readonly DispatcherTimer _poll;

    public WindowStateConfig Persisted { get; } = new();

    public bool IsExpanded
    {
        get => Persisted.Expanded;
        set => ApplyExpanded(value);
    }

    public double Scale
    {
        get => Persisted.Scale;
        set => ApplyScale(value);
    }

    public new bool Topmost
    {
        get => base.Topmost;
        set { base.Topmost = value; Persisted.Topmost = value; }
    }

    public MonitorWindow(WidgetManager manager)
    {
        InitializeComponent();
        _manager = manager;
        DataContext = _vm;
        Deactivated += (_, _) => CloseSelectorPopup();

        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _poll.Tick += (_, _) => _vm.PollLatest();
        _poll.Start();

        LocationChanged += (_, _) => SaveBounds();
        SizeChanged += (_, _) => SaveBounds();

        ApplyScale(1.0);
        ApplyExpanded(false);
    }

    // ------------------------------------------------------------- binding

    /// <summary>Bind this widget to a backend target (shared collector).</summary>
    public void Bind(BackendRegistry.TargetEntry entry)
    {
        var collector = _manager.Registry.Collectors.GetOrAdd(entry.Target.Endpoint, KindOrNull(entry));
        _vm.Bind(collector, entry);
        Persisted.BackendId = entry.Target.GroupKey;
        UpdateHeaderFromEntry(entry);
    }

    private static Core.BackendKind? KindOrNull(BackendRegistry.TargetEntry e) =>
        e.Target.Kind == Core.BackendKind.Unknown ? null : e.Target.Kind;

    public void Unbind()
    {
        _vm.Unbind();
        HeaderText.Text = "LLM Meter";
        SubtitleText.Text = "select a backend";
    }

    /// <summary>Shown while discovery hasn't found the backend yet.</summary>
    public void ShowScanning()
    {
        HeaderText.Text = "LLM Meter";
        SubtitleText.Text = "scanning for servers…";
    }

    private void UpdateHeaderFromEntry(BackendRegistry.TargetEntry entry)
    {
        string name = entry.ModelName is { Length: > 0 } m
            ? $"{entry.Target.Kind.DisplayName()} · {m}"
            : entry.Target.DisplayName;
        if (name.Length > 42) name = name[..41].TrimEnd() + "…";
        HeaderText.Text = name;
        SubtitleText.Text = DescribeOrigin(entry);
    }

    internal static string DescribeOrigin(BackendRegistry.TargetEntry entry)
    {
        var ep = entry.Target.Endpoint;
        return entry.Target.Endpoint.Origin switch
        {
            OriginKind.Wsl => $"WSL · {ep.WslDistro} · :{ep.BaseUrl.Port}",
            OriginKind.Manual => $"Manual · {ep.BaseUrl.Host}:{ep.BaseUrl.Port}",
            _ => $"Windows · 127.0.0.1:{ep.BaseUrl.Port}",
        };
    }

    // ------------------------------------------------------------ controls

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
        {
            try { DragMove(); } catch { }
            SaveBounds();
        }
    }

    private void OnToggleExpand(object sender, RoutedEventArgs e) => ApplyExpanded(!IsExpanded);

    private void OnNewWindow(object sender, RoutedEventArgs e) => _manager.CreateWindow(null);

    private void OnCloseWidget(object sender, RoutedEventArgs e) => _manager.CloseWidget(this);

    private void OnOpenSelector(object sender, RoutedEventArgs e) => ShowSelectorPopup();

    private void OnShowHelp(object sender, RoutedEventArgs e)
    {
        var help = _vm.CurrentHelp ?? _manager.Registry.GetHelpFor(_vm.Collector!);
        if (help == null) return;
        new TelemetryHelpWindow(help) { Owner = this }.ShowNear(this);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            int idx = NearestScaleIndex(Scale);
            idx += e.Delta > 0 ? 1 : -1;
            idx = Math.Clamp(idx, 0, ScaleSteps.Length - 1);
            ApplyScale(ScaleSteps[idx]);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                StepScale(+1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract:
                StepScale(-1); e.Handled = true; break;
            case Key.N:
                _manager.CreateWindow(null); e.Handled = true; break;
        }
    }

    private void StepScale(int dir)
    {
        int idx = Math.Clamp(NearestScaleIndex(Scale) + dir, 0, ScaleSteps.Length - 1);
        ApplyScale(ScaleSteps[idx]);
    }

    private static int NearestScaleIndex(double s)
    {
        int best = 0; double bestDiff = double.MaxValue;
        for (int i = 0; i < ScaleSteps.Length; i++)
        {
            double d = Math.Abs(ScaleSteps[i] - s);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        return best;
    }

    internal void ApplyScale(double value)
    {
        value = ScaleSteps.Aggregate((a, b) => Math.Abs(b - value) < Math.Abs(a - value) ? b : a);
        RootScale.ScaleX = RootScale.ScaleY = value;
        Persisted.Scale = value;
        SaveBounds();
    }

    private void ApplyExpanded(bool expanded)
    {
        Persisted.Expanded = expanded;
        RequestsSeparator.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RequestsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        BottomStats.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = expanded ? "︽" : "︾";
        ExpandButton.ToolTip = expanded ? "Collapse" : "Expand details";
        _vm.IncludeDetails = expanded;
        _vm.PollLatest(force: true);
    }

    internal void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        Persisted.X = Left;
        Persisted.Y = Top;
    }

    // ------------------------------------------------------ context menu

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();

        var topmost = new MenuItem { Header = "Always on Top", IsCheckable = true, IsChecked = Topmost };
        topmost.Click += (_, _) => Topmost = topmost.IsChecked;
        menu.Items.Add(topmost);

        var scale = new MenuItem { Header = "Scale" };
        foreach (var s in ScaleSteps)
        {
            var mi = new MenuItem
            {
                Header = $"{s * 100:0}%",
                IsCheckable = true,
                IsChecked = Math.Abs(s - Scale) < 0.001,
            };
            double captured = s;
            mi.Click += (_, _) => ApplyScale(captured);
            scale.Items.Add(mi);
        }
        menu.Items.Add(scale);

        menu.Items.Add(new Separator());
        var expand = new MenuItem { Header = IsExpanded ? "Collapse" : "Expand", InputGestureText = "" };
        expand.Click += (_, _) => ApplyExpanded(!IsExpanded);
        menu.Items.Add(expand);

        var rescan = new MenuItem { Header = "Rescan Servers" };
        rescan.Click += (_, _) => App.Services.Registry.Discovery.TriggerScan();
        menu.Items.Add(rescan);

        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = "Hide" };
        hide.Click += (_, _) => Hide();
        menu.Items.Add(hide);

        var unbind = new MenuItem { Header = "Unbind Backend" };
        unbind.Click += (_, _) => Unbind();
        menu.Items.Add(unbind);

        ContextMenu = menu;
    }

    // ----------------------------------------------------------- selector

    private PopupHost? _popupHost;

    private void ShowSelectorPopup()
    {
        CloseSelectorPopup();
        _popupHost = new PopupHost(_manager, this);
        _popupHost.Closed += () => _popupHost = null;
        _popupHost.ShowBelow(this);
    }

    private void CloseSelectorPopup() => _popupHost?.Dismiss();

    // ------------------------------------------------------------- close

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing a widget hides it (the app lives in the tray) unless this is
        // an explicit widget removal or app shutdown.
        if (!_realClose && !_manager.IsShuttingDown && Visibility == Visibility.Visible)
        {
            e.Cancel = true;
            Persisted.Visible = false;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private bool _realClose;

    /// <summary>Removes this widget for good (✕ button): no hide, no restore.</summary>
    public void RequestRealClose()
    {
        _realClose = true;
        _poll.Stop();
        Close();
    }

    internal void RestorePersisted(WindowStateConfig cfg)
    {
        Persisted.BackendId = cfg.BackendId;
        Topmost = cfg.Topmost;

        // Defensive bounds restore (display may have changed).
        double x = double.IsNaN(cfg.X) ? 100 : cfg.X;
        double y = double.IsNaN(cfg.Y) ? 100 : cfg.Y;
        var spot = ScreenGuard.EnsureVisible(x, y, 320, 120);
        Left = spot.X;
        Top = spot.Y;
        WindowStartupLocation = WindowStartupLocation.Manual;

        ApplyScale(cfg.Scale <= 0 ? 1.0 : cfg.Scale);
        ApplyExpanded(cfg.Expanded);

        if (cfg.Visible) Show();
    }
}
