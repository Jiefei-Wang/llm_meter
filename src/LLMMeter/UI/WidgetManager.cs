using System.IO;
using System.Windows;
using LLMMeter.Core;
using LLMMeter.Discovery;
using LLMMeter.Persistence;

namespace LLMMeter.UI;

/// <summary>
/// Owns monitor windows: creation, restore, persistence, and shared services.
/// </summary>
public sealed class WidgetManager
{
    public AppServices Services { get; }
    private readonly List<MonitorWindow> _windows = [];
    private readonly List<MonitorWindow> _pendingAutoBind = [];

    /// <summary>Safety cap for auto-spawned widgets.</summary>
    public const int MaxWindows = 6;

    public bool IsShuttingDown { get; set; }
    public BackendRegistry Registry => Services.Registry;

    public WidgetManager(AppServices services)
    {
        Services = services;
        // Discovery raises this on a threadpool thread; hop to the UI thread.
        Registry.TargetsChanged += () => Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal, AutoBindPendingWindows);
    }

    // ------------------------------------------------------------ creation

    public MonitorWindow CreateWindow(WindowConfig? restore)
    {
        var w = new MonitorWindow(this);
        _windows.Add(w);

        if (restore != null)
        {
            w.RestorePersisted(restore);
        }
        else
        {
            // cascade near the last visible window
            double x = 120 + (_windows.Count - 1) * 28;
            double y = 140 + (_windows.Count - 1) * 24;
            var spot = ScreenGuard.EnsureVisible(x, y, 320, 130);
            w.Left = spot.X; w.Top = spot.Y;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Topmost = Services.Config.TopmostByDefault;
            w.Show();
        }

        BindRestoredBackend(w, restore?.BackendId);

        // No explicit selection: let the first suitable discovered backend claim it.
        if (string.IsNullOrEmpty(w.Persisted.BackendId))
        {
            _pendingAutoBind.Add(w);
            AutoBindPendingWindows();
        }

        QueueSave();
        return w;
    }

    /// <summary>
    /// Binds still-unbound widgets to discovered backends nobody else shows yet,
    /// then spawns additional widgets for discovered backends no window claims
    /// (up to MaxWindows). Runs on the UI thread. Hidden windows keep their claims.
    /// </summary>
    internal void AutoBindPendingWindows()
    {
        var entries = Registry.GetTargetEntries();
        if (entries.Count == 0) return;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in _windows)
            if (!string.IsNullOrEmpty(w.Persisted.BackendId))
                used.Add(w.Persisted.BackendId);

        bool changed = false;

        // 1. Fill unbound windows with unclaimed backends.
        foreach (var w in _pendingAutoBind.ToArray())
        {
            if (!string.IsNullOrEmpty(w.Persisted.BackendId))
            {
                _pendingAutoBind.Remove(w); // bound manually meanwhile
                continue;
            }

            var entry = entries.FirstOrDefault(e => !used.Contains(e.Target.GroupKey));
            if (entry == null) break;

            w.Bind(entry);
            used.Add(entry.Target.GroupKey);
            _pendingAutoBind.Remove(w);
            changed = true;
        }

        // 2. One widget per discovered backend: cover orphans with new windows.
        foreach (var entry in entries)
        {
            if (used.Contains(entry.Target.GroupKey)) continue;
            if (_windows.Count >= MaxWindows) break;
            CreateWindowForEntry(entry);
            used.Add(entry.Target.GroupKey);
            changed = true;
        }

        if (changed) QueueSave();
    }

    private void CreateWindowForEntry(BackendRegistry.TargetEntry entry)
    {
        var w = new MonitorWindow(this);
        _windows.Add(w);

        double x = 120 + (_windows.Count - 1) * 28;
        double y = 140 + (_windows.Count - 1) * 24;
        var spot = ScreenGuard.EnsureVisible(x, y, 320, 130);
        w.Left = spot.X; w.Top = spot.Y;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Show();
        w.Bind(entry);
    }

    /// <summary>Rebind to a saved backend id once discovery knows it.</summary>
    private void BindRestoredBackend(MonitorWindow w, string? backendId)
    {
        if (string.IsNullOrEmpty(backendId)) return;

        bool tryBind()
        {
            var entry = FindEntry(backendId);
            if (entry == null) return false;
            w.Bind(entry);
            return true;
        }

        if (tryBind()) return;

        // TargetsChanged fires on a threadpool thread; w.Bind touches WPF,
        // so hop to the UI thread before touching the window.
        Registry.TargetsChanged += onTargets;
        void onTargets()
        {
            Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Normal, () =>
                {
                    if (tryBind())
                        Registry.TargetsChanged -= onTargets;
                });
        }
    }

    private BackendRegistry.TargetEntry? FindEntry(string backendIdOrGroupKey)
    {
        foreach (var e in Registry.GetTargetEntries())
        {
            if (e.Target.GroupKey == backendIdOrGroupKey || e.Target.Id == backendIdOrGroupKey)
                return e;
        }
        return null;
    }

    public IReadOnlyList<MonitorWindow> Windows => _windows;

    /// <summary>Removes a widget permanently (✕ button) and persists the change.</summary>
    public void CloseWidget(MonitorWindow w)
    {
        _windows.Remove(w);
        _pendingAutoBind.Remove(w);
        w.RequestRealClose();
        QueueSave();
    }

    public void RestoreFromConfig(AppConfiguration cfg)
    {
        var list = cfg.Windows;
        if (list.Count == 0)
        {
            CreateWindow(null);
            return;
        }
        foreach (var wc in list.Take(8))
            CreateWindow(wc);
    }

    public void ShowAddBackendDialog()
    {
        var dlg = new AddBackendDialog(Services) { Owner = null };
        dlg.ShowDialog();
    }

    public void ShowSettingsDialog()
    {
        new SettingsDialog(Services).ShowDialog();
    }

    public void ToggleAllWindows()
    {
        // All widgets closed via ✕: bring the app back by creating a fresh one.
        if (_windows.Count == 0)
        {
            CreateWindow(null);
            return;
        }

        bool anyVisible = _windows.Any(w => w.IsVisible);
        foreach (var w in _windows)
        {
            if (anyVisible) { w.Persisted.Visible = false; w.Hide(); }
            else { w.Persisted.Visible = true; w.Show(); }
        }
        QueueSave();
    }

    // ---------------------------------------------------------- persistence

    public AppConfiguration CaptureWindowState(AppConfiguration cfg)
    {
        cfg.Windows = [.. _windows.Select(w => new WindowConfig
        {
            BackendId = w.Persisted.BackendId,
            X = w.Persisted.X,
            Y = w.Persisted.Y,
            Scale = w.Persisted.Scale,
            Expanded = w.Persisted.Expanded,
            Topmost = w.Persisted.Topmost,
            Visible = w.IsVisible,
        })];
        return cfg;
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    public void QueueSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try
        {
            var cfg = CaptureWindowState(Services.Config);
            Services.ConfigService.Save(cfg);
        }
        catch (Exception ex)
        {
            Log.Warn($"config save failed: {ex.Message}");
        }
    }
}

/// <summary>Process-wide services wired at startup.</summary>
public sealed class AppServices : IDisposable
{
    public required ConfigurationService ConfigService { get; init; }
    public required AppConfiguration Config { get; init; }
    public required BackendRegistry Registry { get; init; }
    public WidgetManager Widgets { get; set; } = null!;
    public TrayService Tray { get; set; } = null!;

    public void Dispose() => Registry.Dispose();
}
