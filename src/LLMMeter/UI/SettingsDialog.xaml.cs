using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LLMMeter.Core;

namespace LLMMeter.UI;

public partial class SettingsDialog : Window
{
    private readonly AppServices _services;

    public SettingsDialog(AppServices services)
    {
        InitializeComponent();
        _services = services;
        var d = services.Config.Discovery;

        DiscoveryCheck.IsChecked = d.Enabled;
        WslCheck.IsChecked = d.WslEnabled;
        ListenerCheck.IsChecked = d.WindowsListeners;

        LoggingCheck.IsChecked = Log.Enabled;
        ConfigPathText.Text = services.ConfigService.ConfigPath;
        TopmostCheck.IsChecked = services.Config.TopmostByDefault;

        // manual endpoints list with delete buttons
        RefreshManualList();
    }

    private void RefreshManualList()
    {
        ManualList.Items.Clear();
        foreach (var m in _services.Registry.ManualEndpoints)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var del = new Button
            {
                Content = "✕",
                Width = 24,
                ToolTip = "Remove this endpoint",
            };
            string url = m.Url;
            del.Click += (_, _) =>
            {
                _services.Registry.RemoveManualEndpoint(url);
                RefreshManualList();
            };
            DockPanel.SetDock(del, Dock.Right);
            panel.Children.Add(del);
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(m.Name) ? m.Url : $"{m.Name}  —  {m.Url}",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            ManualList.Items.Add(panel);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var cfg = _services.Config;
        cfg.Discovery.Enabled = DiscoveryCheck.IsChecked == true;
        cfg.Discovery.WslEnabled = WslCheck.IsChecked == true;
        cfg.Discovery.WindowsListeners = ListenerCheck.IsChecked == true;

        if (LoggingCheck.IsChecked == true && !Log.Enabled)
            Log.Enable();
        else if (LoggingCheck.IsChecked != true && Log.Enabled)
            Log.Disable();

        cfg.TopmostByDefault = TopmostCheck.IsChecked == true;

        _services.Widgets.SaveNow();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_services.ConfigService.ConfigPath}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void OnRescanNow(object sender, RoutedEventArgs e) =>
        _services.Registry.Discovery.TriggerScan();
}
