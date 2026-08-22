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

    public bool IsShuttingDown { get; set; }
    public BackendRegistry Registry => Services.Registry;

    public WidgetManager(AppServices services) => Services = services;

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
            w.Show();
        }

        BindRestoredBackend(w, restore?.BackendId);
        QueueSave();
        return w;
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

        Registry.TargetsChanged += onTargets;
        void onTargets()
        {
            if (tryBind())
                Registry.TargetsChanged -= onTargets;
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
