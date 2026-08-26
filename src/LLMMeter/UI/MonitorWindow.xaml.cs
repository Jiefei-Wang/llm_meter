using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LLMMeter.Core;
using WindowStateConfig = LLMMeter.Persistence.WindowConfig;

namespace LLMMeter.UI;

public partial class MonitorWindow : Window
{
    public static readonly double[] ScaleSteps = [0.75, 0.90, 1.00, 1.10, 1.25, 1.50, 1.75, 2.00];

    private readonly WidgetManager _manager;
    private readonly MonitorWindowViewModel _vm = new();

    public WindowStateConfig Persisted { get; } = new();

    public bool IsExpanded
    {
        get => Persisted.Expanded;
        set => ApplyExpanded(value);
    }

    public double Scale
    {
        get => Persisted.Scale;
        set => ApplyScale(value, snap: false);
    }

    public bool IsAlwaysOnTop => Persisted.Topmost;

    public MonitorWindow(WidgetManager manager)
    {
        InitializeComponent();
        _manager = manager;
        DataContext = _vm;
        _vm.RequestRows.CollectionChanged += (_, _) => GrowRequestViewportForRows();
        ContextMenu = new ContextMenu();
        Deactivated += (_, _) => CloseSelectorPopup();
        SourceInitialized += (_, _) =>
        {
            ApplyTopmostBand();
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowMessageHook);
        };
        IsVisibleChanged += (_, _) =>
        {
            _vm.SetRenderingActive(IsVisible);
            if (IsVisible)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    ApplyTopmostBand);
        };

        LocationChanged += (_, _) => SaveBounds();
        SizeChanged += (_, _) => SaveBounds();
        AddHandler(Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnResizeStart), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(OnResizeMove), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnResizeEnd), handledEventsToo: true);

        ApplyScale(1.0);
        ApplyExpanded(false);
    }

    // ------------------------------------------------------------- binding

    /// <summary>Bind this widget to a backend target (shared collector).</summary>
    public void Bind(BackendRegistry.TargetEntry entry)
    {
        if (!string.IsNullOrEmpty(Persisted.BackendId) &&
            !Persisted.BackendId.Equals(entry.Target.GroupKey, StringComparison.OrdinalIgnoreCase))
        {
            Persisted.GeneratedUsageBaseline = null;
            Persisted.PrefilledUsageBaseline = null;
            _vm.SetUsageBaselines(null, null);
        }
        var modelId = entry.Target.RequiresModelScopedCollector ? entry.Target.ModelId : null;
        var collector = _manager.Registry.Collectors.Acquire(entry.Target.Endpoint, KindOrNull(entry), modelId);
        _vm.Bind(collector, entry, () => _manager.Registry.Collectors.Release(collector));
        Persisted.BackendId = entry.Target.GroupKey;
        _manager.QueueSave();
    }


    private static Core.BackendKind? KindOrNull(BackendRegistry.TargetEntry e) =>
        e.Target.Kind == Core.BackendKind.Unknown ? null : e.Target.Kind;

    public void Unbind()
    {
        _vm.Unbind();
        Persisted.BackendId = "";
        _manager.QueueSave();
    }

    /// <summary>Shown while discovery hasn't found the backend yet.</summary>
    public void ShowScanning()
    {
        _vm.ShowScanning();
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
        if (_resizeEdge == ResizeEdge.None &&
            e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
        {
            try { DragMove(); } catch { }
            SaveBounds();
        }
    }

    private void OnToggleExpand(object sender, RoutedEventArgs e) => ApplyExpanded(!IsExpanded);

    private void OnResizeRequestList(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        SetRequestViewportHeight(RequestViewport.Height + e.VerticalChange, queueSave: true);
        e.Handled = true;
        SaveBounds();
    }

    private const double RequestRowHeight = 37;
    private const int AutomaticVisibleRequestRows = 5;

    private void GrowRequestViewportForRows()
    {
        // Collection removals intentionally never reduce Height. This retained
        // high-water mark prevents the whole widget jumping upward as trailing
        // request slots age out.
        double required = Math.Min(_vm.RequestRows.Count, AutomaticVisibleRequestRows) * RequestRowHeight;
        if (required > RequestViewport.Height)
            SetRequestViewportHeight(required, queueSave: true);
    }

    private void SetRequestViewportHeight(double height, bool queueSave)
    {
        height = Math.Clamp(height, 0, 740);
        RequestViewport.Height = height;
        Persisted.RequestListHeight = height;
        if (queueSave) _manager.QueueSave();
    }

    private void OnShowPrefillHistory(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleActivity(PrefillMetricContent, PrefillActivityChart);
    }

    private void OnShowGenerateHistory(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleActivity(GenerateMetricContent, GenerateActivityChart);
    }

    private static void ToggleActivity(UIElement metric, UIElement chart)
    {
        bool showChart = chart.Visibility != Visibility.Visible;
        metric.Visibility = showChart ? Visibility.Collapsed : Visibility.Visible;
        chart.Visibility = showChart ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e) =>
        SetAlwaysOnTop(PinButton.IsChecked == true);

    private void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        BuildContextMenu();
        ContextMenu.PlacementTarget = MenuButton;
        ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ContextMenu.IsOpen = true;
    }

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

    internal void ApplyScale(double value, bool snap = true)
    {
        value = Math.Clamp(value, 0.65, 2.0);
        if (snap)
            value = ScaleSteps.Aggregate((a, b) => Math.Abs(b - value) < Math.Abs(a - value) ? b : a);
        RootScale.ScaleX = RootScale.ScaleY = value;
        Persisted.Scale = value;
        SaveBounds();
    }

    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private ResizeEdge _resizeEdge;
    private Point _resizeStartScreen;
    private double _resizeStartScale;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeStartRight;
    private double _resizeStartBottom;

    private void OnResizeStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _resizeEdge != ResizeEdge.None) return;
        var edge = HitTestResizeEdge(e.GetPosition(RootBorder));
        if (edge == ResizeEdge.None) return;

        _resizeEdge = edge;
        _resizeStartScreen = ScreenDip(e);
        _resizeStartScale = Scale;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _resizeStartRight = Left + ActualWidth;
        _resizeStartBottom = Top + ActualHeight;
        // Capture the complete widget subtree rather than the HWND-backed Window.
        // Capturing the Window is fragile when the pointer starts over a child
        // control: WPF may transfer capture and end the resize immediately.
        Mouse.Capture(RootBorder, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void OnResizeMove(object sender, MouseEventArgs e)
    {
        if (_resizeEdge == ResizeEdge.None)
        {
            Cursor = CursorFor(HitTestResizeEdge(e.GetPosition(RootBorder)));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { EndResize(); return; }

        var current = ScreenDip(e);
        double dx = current.X - _resizeStartScreen.X;
        double dy = current.Y - _resizeStartScreen.Y;
        var ratios = new List<double>(2);
        if (_resizeEdge.HasFlag(ResizeEdge.Left)) ratios.Add((_resizeStartWidth - dx) / _resizeStartWidth);
        if (_resizeEdge.HasFlag(ResizeEdge.Right)) ratios.Add((_resizeStartWidth + dx) / _resizeStartWidth);
        if (_resizeEdge.HasFlag(ResizeEdge.Top)) ratios.Add((_resizeStartHeight - dy) / _resizeStartHeight);
        if (_resizeEdge.HasFlag(ResizeEdge.Bottom)) ratios.Add((_resizeStartHeight + dy) / _resizeStartHeight);
        if (ratios.Count == 0) return;

        ApplyScale(_resizeStartScale * ratios.Average(), snap: false);
        UpdateLayout();
        if (_resizeEdge.HasFlag(ResizeEdge.Left)) Left = _resizeStartRight - ActualWidth;
        if (_resizeEdge.HasFlag(ResizeEdge.Top)) Top = _resizeStartBottom - ActualHeight;
        e.Handled = true;
    }

    private void OnResizeEnd(object sender, MouseButtonEventArgs e)
    {
        if (_resizeEdge == ResizeEdge.None) return;
        EndResize();
        e.Handled = true;
    }

    private void EndResize()
    {
        _resizeEdge = ResizeEdge.None;
        if (Mouse.Captured == RootBorder || RootBorder.IsMouseCaptureWithin)
            Mouse.Capture(null);
        _manager.QueueSave();
    }

    private ResizeEdge HitTestResizeEdge(Point point)
    {
        const double grip = 7;
        ResizeEdge edge = ResizeEdge.None;
        if (point.X >= -grip && point.X <= grip) edge |= ResizeEdge.Left;
        else if (point.X >= RootBorder.ActualWidth - grip && point.X <= RootBorder.ActualWidth + grip) edge |= ResizeEdge.Right;
        if (point.Y >= -grip && point.Y <= grip) edge |= ResizeEdge.Top;
        else if (point.Y >= RootBorder.ActualHeight - grip && point.Y <= RootBorder.ActualHeight + grip) edge |= ResizeEdge.Bottom;
        return edge;
    }

    private static Cursor CursorFor(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
        ResizeEdge.Left | ResizeEdge.Top or ResizeEdge.Right | ResizeEdge.Bottom => Cursors.SizeNWSE,
        ResizeEdge.Right | ResizeEdge.Top or ResizeEdge.Left | ResizeEdge.Bottom => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    private Point ScreenDip(MouseEventArgs e)
    {
        var pixels = PointToScreen(e.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(pixels) ?? pixels;
    }

    private void ApplyExpanded(bool expanded)
    {
        Persisted.Expanded = expanded;
        RequestsSeparator.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RequestsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        BottomStats.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Tag = expanded;
        ExpandButton.ToolTip = expanded ? "Collapse" : "Expand details";
        _vm.IncludeDetails = expanded;
        _vm.PollLatest(force: true);
        _manager.QueueSave();
    }

    internal void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        Persisted.X = Left;
        Persisted.Y = Top;
        _manager.QueueSave();
    }

    // ------------------------------------------------------ context menu

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e) => BuildContextMenu();

    private void BuildContextMenu()
    {
        var menu = ContextMenu ?? new ContextMenu();
        menu.Items.Clear();

        var topmost = new MenuItem { Header = "Always on Top", IsCheckable = true, IsChecked = IsAlwaysOnTop };
        topmost.Click += (_, _) => SetAlwaysOnTop(topmost.IsChecked);
        menu.Items.Add(topmost);

        var newWindow = new MenuItem { Header = "New Monitor Window", InputGestureText = "Ctrl+N" };
        newWindow.Click += OnNewWindow;
        menu.Items.Add(newWindow);

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

        var resetUsage = new MenuItem { Header = "Reset Usage" };
        resetUsage.Click += (_, _) =>
        {
            var baselines = _vm.ResetUsage();
            Persisted.GeneratedUsageBaseline = baselines.Generated;
            Persisted.PrefilledUsageBaseline = baselines.Prefilled;
            _manager.QueueSave();
        };
        menu.Items.Add(resetUsage);

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
        _vm.Dispose();
        Close();
    }

    internal void SetAlwaysOnTop(bool enabled, bool queueSave = true)
    {
        Persisted.Topmost = enabled;
        PinButton.IsChecked = enabled;
        ApplyTopmostBand();
        if (queueSave) _manager.QueueSave();
    }

    private void ApplyTopmostBand() => WindowZOrder.Apply(this, Persisted.Topmost);

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDisplayChange = 0x007E;
        const int WmDwmCompositionChanged = 0x031E;
        if (message is WmDisplayChange or WmDwmCompositionChanged)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ApplyTopmostBand);
        return IntPtr.Zero;
    }

    internal void RestorePersisted(WindowStateConfig cfg)
    {
        Persisted.BackendId = cfg.BackendId;
        Persisted.GeneratedUsageBaseline = cfg.GeneratedUsageBaseline;
        Persisted.PrefilledUsageBaseline = cfg.PrefilledUsageBaseline;
        _vm.SetUsageBaselines(cfg.GeneratedUsageBaseline, cfg.PrefilledUsageBaseline);
        SetAlwaysOnTop(cfg.Topmost, queueSave: false);

        // Defensive bounds restore (display may have changed).
        double x = double.IsNaN(cfg.X) ? 100 : cfg.X;
        double y = double.IsNaN(cfg.Y) ? 100 : cfg.Y;
        var spot = ScreenGuard.EnsureVisible(x, y, 320, 120);
        Left = spot.X;
        Top = spot.Y;
        WindowStartupLocation = WindowStartupLocation.Manual;

        ApplyScale(cfg.Scale <= 0 ? 1.0 : cfg.Scale, snap: false);
        SetRequestViewportHeight(cfg.RequestListHeight > 0 ? cfg.RequestListHeight : 0, queueSave: false);
        ApplyExpanded(cfg.Expanded);

        if (cfg.Visible) Show();
    }
}
