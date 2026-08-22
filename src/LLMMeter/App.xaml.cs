using System.IO;
using System.Windows;
using System.Windows.Interop;
using LLMMeter.Core;
using LLMMeter.Persistence;
using LLMMeter.UI;

namespace LLMMeter;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private static AppServices? _services;

    internal static AppServices Services => _services ?? throw new InvalidOperationException("app not initialized");

    /// <summary>Fetch a themed brush by resource key.</summary>
    public static System.Windows.Media.Brush ThemedBrush(string key) =>
        (System.Windows.Media.Brush)(Application.Current.Resources[key]
            ?? System.Windows.Media.Brushes.Gray);

    public static void RequestShutdown()
    {
        try { _services?.Widgets.SaveNow(); } catch { }
        try { _services?.Dispose(); } catch { }
        try { Current?.Shutdown(); } catch { }
    }

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xB00B;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "LLMMeter_SingleInstance_1F3A", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("LLM Meter is already running. Look for the tray icon.",
                "LLM Meter", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error($"UI exception: {args.Exception.GetType().Name}: {args.Exception.Message}");
            args.Handled = true; // a monitoring widget must not die on a hiccup
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error($"unobserved task exception: {args.Exception.Message}");
            args.SetObserved();
        };

        ThemeManager.Apply(ThemeManager.IsDarkByDefault());

        var configService = new ConfigurationService();
        var config = configService.Load();

        if (configService.LastLoadError != null)
        {
            MessageBox.Show(
                "LLMMeter.json could not be read.\n" +
                (configService.BackupPath != null
                    ? $"The broken file was preserved as {Path.GetFileName(configService.BackupPath)}.\n"
                    : "") +
                "Starting with default settings.\n\n" + configService.LastLoadError,
                "LLM Meter — configuration problem",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (e.Args.Contains("--log"))
            Log.Enable();

        var registry = new BackendRegistry(config, configService);
        var services = new AppServices
        {
            ConfigService = configService,
            Config = config,
            Registry = registry,
            Widgets = null!,
        };
        services.Widgets = new WidgetManager(services);
        _services = services;

        services.Tray = new TrayService(services);
        registry.TargetsChanged += UpdateTrayTooltip;
        registry.Discovery.Start();
        services.Widgets.RestoreFromConfig(config);

        UpdateTrayTooltip();
        RegisterHotkey();
    }

    private void UpdateTrayTooltip()
    {
        try
        {
            var entries = _services!.Registry.GetTargetEntries();
            int online = entries.Count(e => BackendRegistry.IsOnline(
                _services.Registry.Collectors.GetOrAdd(e.Target.Endpoint,
                    e.Target.Kind == BackendKind.Unknown ? null : e.Target.Kind).Latest));
            _services.Tray.UpdateTooltip(online == 0
                ? "LLM Meter — no backend monitored"
                : $"LLM Meter — {online} backend(s) online");
        }
        catch { }
    }

    private void RegisterHotkey()
    {
        try
        {
            ComponentDispatcher.ThreadPreprocessMessage += (ref MSG msg, ref bool handled) =>
            {
                if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
                {
                    _services?.Widgets.ToggleAllWindows();
                    handled = true;
                }
            };
            // MOD_CONTROL | MOD_ALT, 'L'
            if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, 0x4000 | 0x1000, 'L'))
                Log.Warn("global hotkey registration failed");
        }
        catch
        {
            // hotkey unavailable — tray click still works
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { UnregisterHotKey(IntPtr.Zero, HOTKEY_ID); } catch { }
        try { _services?.Widgets.SaveNow(); } catch { }
        try { _services?.Dispose(); } catch { }
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
